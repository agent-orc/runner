using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution.Hardening;
using CodingAgentRunner.Execution.Logging;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Execution;

/// <summary>
/// The spawn engine shared by every CLI driver. It owns the lifecycle a run goes
/// through — resolve binary &#8594; harden environment &#8594; install the git guard
/// &#8594; spawn &#8594; stream stdout/stderr &#8594; map lines to typed
/// <see cref="CliRunEvent"/>s &#8594; classify the exit — and leaves only the
/// CLI-specific bits (the argument list, the optional stdin payload, the frame
/// adapter) to subclasses.
///
/// <para>
/// Completion is the CLI's REAL signal: a <see cref="CliRunEvent.TurnCompleted"/>
/// raised by the subclass's adapter plus the classified
/// <see cref="CliRunEvent.RunEnded"/> from this engine. There is no
/// output-sentinel scraping.
/// </para>
/// </summary>
internal abstract class CliDriverBase : ICliDriver
{
    // Per-run tracking, keyed by CliRunRequest.RunId. Private: the contract is
    // expressed entirely through ICliDriver's events + GetExecution/GetOutput.
    private readonly ConcurrentDictionary<string, ProcInfo> _processes = new();

    /// <summary>Consumer configuration (paths, git-guard, hardening).</summary>
    protected CliOptions Options { get; }

    /// <summary>Diagnostics logger (never null; defaults to a no-op).</summary>
    protected ILogger Logger { get; }

    /// <summary>Where per-run output logs are written.</summary>
    protected IRunLogPathProvider LogPaths { get; }

    /// <summary>User-home / temp-root provider for clean-context homes.</summary>
    protected IUserHomeProvider Home { get; }

    /// <summary>Create the engine with consumer-supplied configuration and providers.</summary>
    protected CliDriverBase(
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null)
    {
        Options = options ?? new CliOptions();
        Logger = logger ?? NullLogger.Instance;
        Home = home ?? new DefaultUserHomeProvider();
        LogPaths = logPaths ?? new DefaultRunLogPathProvider(Home);
    }

    /// <inheritdoc />
    public abstract string CliType { get; }

    /// <inheritdoc />
    public abstract string GetCliPath();

    /// <inheritdoc />
    public virtual bool SupportsCleanContext => false;

    /// <summary>Create a clean per-run config home, or null when unsupported / impossible.</summary>
    public virtual CleanContextPreparation? PrepareCleanContext(string workingDirectory) => null;

    /// <summary>Whether this CLI can resume a session via <see cref="CliRunRequest.ResumeSessionId"/>.</summary>
    protected virtual bool SupportsResume => false;

    /// <summary>
    /// CLI-specific knobs beyond reasoning, advertised through <see cref="Capabilities"/>.
    /// Empty for every built-in CLI; a driver overrides this when it grows a real knob.
    /// </summary>
    protected virtual IReadOnlyDictionary<string, IReadOnlyList<string>> DescribeKnobs(string? model)
        => EmptyKnobs;

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> EmptyKnobs
        = new Dictionary<string, IReadOnlyList<string>>();

    /// <inheritdoc />
    public virtual CliCapabilities Capabilities(string? model) => new()
    {
        CliType = CliType,
        Model = string.IsNullOrWhiteSpace(model) ? null : model.Trim(),
        ThinkingLevels = CliThinkingLevels.For(CliType, model),
        DefaultThinkingLevel = CliThinkingLevels.DefaultFor(CliType, model),
        SupportsCleanContext = SupportsCleanContext,
        SupportsResume = SupportsResume,
        Knobs = DescribeKnobs(model),
    };

    // ── Events ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public event Action<string, CliOutputLine>? OnOutput;
    /// <inheritdoc />
    public event Action<string, CliRunInfo>? OnStarted;
    /// <inheritdoc />
    public event Action<string, CliRunInfo>? OnFinished;
    /// <inheritdoc />
    public event Action<string, CliRunEvent>? OnRunEvent;

    /// <summary>Raise a typed run event, guarding subscriber exceptions.</summary>
    protected void RaiseRunEvent(string runId, CliRunEvent evt)
    {
        try { OnRunEvent?.Invoke(runId, evt); }
        catch (Exception ex) { Logger.LogWarning(ex, "OnRunEvent subscriber threw for {RunId}", runId); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CliRunEvent> StreamAsync(
        CliRunRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A pull-stream over the same event source: subscribe a private handler that
        // funnels THIS run's events into an unbounded channel, then yield them. The
        // run's terminal event (RunEnded) completes the channel.
        var runId = request.RunId;
        var channel = Channel.CreateUnbounded<CliRunEvent>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

        void Funnel(string id, CliRunEvent evt)
        {
            if (id != runId) return;                                  // multiplex filter
            channel.Writer.TryWrite(evt);
            if (evt is CliRunEvent.RunEnded)
                channel.Writer.TryComplete();                        // terminal → close stream
        }

        OnRunEvent += Funnel;
        try
        {
            var (_, error) = await StartAsync(request, ct).ConfigureAwait(false);
            if (error is not null)
                channel.Writer.TryComplete(new InvalidOperationException(error));

            await foreach (var evt in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                yield return evt;
        }
        finally
        {
            OnRunEvent -= Funnel;
            channel.Writer.TryComplete();        // on an early break the channel state matches reality
            if (ct.IsCancellationRequested) Stop(runId, RunStopReason.UserStop);
        }
    }

    // ── Availability ────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool IsAvailable() => TestCliPath().Available;

    /// <inheritdoc />
    public virtual (bool Available, string? Version, string Path) TestCliPath(string? path = null)
    {
        var target = string.IsNullOrWhiteSpace(path) ? GetCliPath() : path!;
        string resolved;
        try { resolved = BinaryResolver.ResolveExecutable(target); }
        catch { resolved = target; }

        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = resolved,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return (false, null, resolved);
            if (!p.WaitForExit(8000)) { try { p.Kill(entireProcessTree: true); } catch { } return (false, null, resolved); }
            var version = p.StandardOutput.ReadToEnd().Trim();
            return (p.ExitCode == 0, string.IsNullOrWhiteSpace(version) ? null : version, resolved);
        }
        catch
        {
            return (false, null, resolved);
        }
    }

    // ── Subclass hooks ──────────────────────────────────────────────────

    /// <summary>Build the CLI-specific <see cref="ProcessStartInfo"/> (FileName + ArgumentList).</summary>
    protected abstract ProcessStartInfo BuildStartInfo(CliRunRequest request, string? model, string? thinkingLevel);

    /// <summary>Test hook: build the start info exactly as <see cref="StartAsync"/> would (model + thinking normalized).</summary>
    internal ProcessStartInfo BuildStartInfoForTest(CliRunRequest request)
    {
        var model = NormalizeModelForInvocation(request.Model);
        var thinking = CliThinkingLevels.Normalize(CliType, model, request.ThinkingLevel);
        return BuildStartInfo(request, model, thinking);
    }

    /// <summary>Test hook: the stdin payload this driver would pipe for the request (model normalized).</summary>
    internal string? BuildPromptStdinPayloadForTest(CliRunRequest request)
        => GetPromptStdinPayload(request, NormalizeModelForInvocation(request.Model));

    /// <summary>
    /// The payload to pipe to the child's stdin, or null to leave stdin default-deny
    /// (the prompt then lives in argv). Default: null.
    /// </summary>
    protected virtual string? GetPromptStdinPayload(CliRunRequest request, string? model) => null;

    /// <summary>
    /// Normalize a model id before it reaches argv. Default: trim / null-empty.
    /// <para>An override MUST preserve enough of the id (family/version tokens) for
    /// <see cref="CliThinkingLevels"/> to still recognize it — the normalized value is
    /// also what resolves the reasoning ladder.</para>
    /// </summary>
    protected virtual string? NormalizeModelForInvocation(string? model)
        => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    /// <summary>
    /// Map one raw stdout/stderr line to zero or more <see cref="CliRunEvent"/>s.
    /// Default: nothing (a driver without an adapter rides the silence watchdog).
    /// </summary>
    protected virtual IEnumerable<CliRunEvent> MapLineToRunEvents(string runId, CliOutputLine line)
        => Array.Empty<CliRunEvent>();

    /// <summary>An optional pre-spawn health check / self-heal. Default: healthy.</summary>
    protected virtual Task<(bool Ok, string? Error)> EnsureCliHealthyAsync(CancellationToken ct)
        => Task.FromResult((true, (string?)null));

    /// <summary>
    /// Spawn the child. Default uses <see cref="Process"/> with redirected pipes;
    /// PTY-based drivers override to keep a Node CLI's stdout line-flushing.
    /// </summary>
    protected virtual Task<ChildHandle> SpawnChildAsync(ProcessStartInfo psi, CliRunRequest request, string? model, CancellationToken ct)
    {
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.Start();
        var stdin = psi.RedirectStandardInput ? p.StandardInput.BaseStream : Stream.Null;
        return Task.FromResult(new ChildHandle(p, stdin, p.StandardOutput, p.StandardError));
    }

    // ── Start ───────────────────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<(CliRunInfo? Run, string? Error)> StartAsync(CliRunRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Input validation (fail fast, clear error) before touching process state.
        if (string.IsNullOrWhiteSpace(request.RunId))
            return (null, "RunId must be a non-empty id, unique per live run.");
        if (string.IsNullOrWhiteSpace(request.WorkingDirectory) || !Directory.Exists(request.WorkingDirectory))
            return (null, $"WorkingDirectory does not exist: '{request.WorkingDirectory}'.");
        if (request.Tuning is not null)
            foreach (var kv in request.Tuning)
                if (string.IsNullOrWhiteSpace(kv.Key))
                    return (null, "Tuning keys cannot be empty.");

        if (_processes.TryGetValue(request.RunId, out var existing))
        {
            if (!SafeHasExited(existing.Process))
                return (null, $"{CliType} run already in flight for '{request.RunId}'");
            _processes.TryRemove(request.RunId, out _);
        }

        var (healthy, healError) = await EnsureCliHealthyAsync(ct).ConfigureAwait(false);
        if (!healthy)
        {
            Logger.LogError("Pre-spawn health check failed for {Cli} ({RunId}): {Error}", CliType, request.RunId, healError);
            return (null, $"{CliType} CLI not available: {healError}");
        }

        var model = NormalizeModelForInvocation(request.Model);
        var thinking = CliThinkingLevels.Normalize(CliType, model, request.ThinkingLevel);

        var psi = BuildStartInfo(request, model, thinking);
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.WorkingDirectory = request.WorkingDirectory;

        var stdinPayload = GetPromptStdinPayload(request, model);
        psi.RedirectStandardInput = !string.IsNullOrEmpty(stdinPayload);

        EnvironmentHardening.Apply(psi, Options.Hardening, Options.EnvironmentOverrides);
        if (request.ExtraEnvironment is not null)
            foreach (var kv in request.ExtraEnvironment) psi.Environment[kv.Key] = kv.Value;

        CleanContextPreparation? cleanContext = null;
        if (CliContextModes.Normalize(request.ContextMode) == CliContextModes.Clean && SupportsCleanContext)
        {
            cleanContext = PrepareCleanContext(request.WorkingDirectory);
            if (cleanContext != null)
            {
                foreach (var kv in cleanContext.EnvOverrides) psi.Environment[kv.Key] = kv.Value;
                Logger.LogInformation("{Cli} clean context for {RunId}: isolated home at {Home}", CliType, request.RunId, cleanContext.TempHome);
            }
        }

        AgentGitCommandGuard.Apply(psi, Options.GitGuard, Options.AllowAgentGitMutation, Logger);

        ChildHandle child;
        try
        {
            child = await SpawnChildAsync(psi, request, model, ct).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(stdinPayload))
            {
                try
                {
                    var bytes = Encoding.UTF8.GetBytes(stdinPayload);
                    await child.Stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await child.Stdin.FlushAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) { Logger.LogWarning(ex, "Failed to write stdin payload for {Cli} {RunId}", CliType, request.RunId); }
                finally { try { child.Stdin.Close(); } catch { } }
            }
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to start {Cli} CLI for {RunId}", CliType, request.RunId);
            cleanContext?.Dispose();
            return (null, $"Failed to start {CliType} CLI: {ex.Message}");
        }

        var process = child.Process;
        var run = new CliRunInfo
        {
            RunId = request.RunId,
            ProcessId = SafePid(process),
            StartedAt = DateTime.UtcNow,
            Status = "running",
            Model = model,
            ThinkingLevel = thinking,
        };

        var logDir = LogPaths.GetRunLogDirectory(request.RunId);
        var info = new ProcInfo(process, run, request.WorkingDirectory)
        {
            OutputLog = new RunLogStore(logDir),
            ChildStdin = child.Stdin,
            KillOverride = child.KillOverride,
            CleanContext = cleanContext,
            LastStreamedAt = run.StartedAt,
        };
        try { info.OutputLog.Reset(); }
        catch (Exception ex) { Logger.LogWarning(ex, "Failed to reset output log dir {Path}", logDir); }
        _processes[request.RunId] = info;

        try { OnStarted?.Invoke(request.RunId, run); }
        catch (Exception ex) { Logger.LogWarning(ex, "OnStarted subscriber threw for {RunId}", request.RunId); }
        RaiseRunEvent(request.RunId, new CliRunEvent.RunStarted(run.ProcessId, CliType, model) { RunId = request.RunId });

        Logger.LogInformation("Started {Cli} CLI for {RunId} (PID {Pid}) in {Cwd}", CliType, request.RunId, run.ProcessId, request.WorkingDirectory);

        // Synthetic "started" line so a consumer's live view is not empty during the
        // window between spawn and the CLI's first byte (claude -p buffers output).
        var startedLine = new CliOutputLine
        {
            Timestamp = DateTime.UtcNow,
            Stream = "system",
            Text = $"[runner] Started {CliType} CLI (PID {run.ProcessId})"
                   + (string.IsNullOrWhiteSpace(model) ? "" : $", model={model}")
                   + (string.IsNullOrWhiteSpace(thinking) ? "" : $", thinking={thinking}")
                   + (string.IsNullOrWhiteSpace(request.ResumeSessionId) ? "" : " (resume)"),
        };
        lock (info.OutputBuffer) info.OutputBuffer.Add(startedLine);
        info.OutputLog.Append(startedLine);
        try { OnOutput?.Invoke(request.RunId, startedLine); } catch { }

        info.StdoutReadTask = ReadStreamAsync(request.RunId, child.Stdout, "stdout", info, ct);
        info.StderrReadTask = ReadStreamAsync(request.RunId, child.Stderr, "stderr", info, ct);
        _ = MonitorProcessAsync(request.RunId, process, info, ct);

        return (run, null);
    }

    // ── Stream + monitor ────────────────────────────────────────────────

    private async Task ReadStreamAsync(string runId, StreamReader reader, string stream, ProcInfo info, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;

                var raw = new CliOutputLine { Timestamp = DateTime.UtcNow, Stream = stream, Text = line };

                info.OutputLog.Append(raw);
                info.LastStreamedAt = DateTime.UtcNow;

                IEnumerable<CliRunEvent>? events = null;
                try { events = MapLineToRunEvents(runId, raw); }
                catch (Exception ex) { Logger.LogWarning(ex, "MapLineToRunEvents threw for {RunId}; skipping typed events for this line", runId); }
                if (events != null)
                    foreach (var evt in events)
                    {
                        // Remember the last turn-failure so RunEnded.Reason can carry it on a Failed outcome.
                        if (evt is CliRunEvent.TurnFailed tf) info.LastFailureReason = tf.Reason;
                        RaiseRunEvent(runId, evt);
                    }

                lock (info.OutputBuffer)
                {
                    info.OutputBuffer.Add(raw);
                    while (info.OutputBuffer.Count > 5000) info.OutputBuffer.RemoveAt(0);
                }
                try { OnOutput?.Invoke(runId, raw); }
                catch (Exception ex) { Logger.LogWarning(ex, "OnOutput subscriber threw for {RunId}", runId); }
            }
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error reading {Stream} for {Cli} {RunId}", stream, CliType, runId);
        }
    }

    private async Task MonitorProcessAsync(string runId, Process process, ProcInfo info, CancellationToken ct)
    {
        try
        {
            try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { Stop(runId, RunStopReason.Cancelled); }

            // Drain the read loops before finalizing: WaitForExitAsync returns as
            // soon as the OS notices the child is gone, but the OS pipe still holds
            // bytes the CLI wrote just before exit. Cap the wait so a stuck read
            // cannot pin the exit.
            try
            {
                await Task.WhenAll(info.StdoutReadTask ?? Task.CompletedTask, info.StderrReadTask ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException) { Logger.LogWarning("Read-stream drain timed out for {RunId}; some output may be missing", runId); }
            catch (Exception ex) { Logger.LogDebug(ex, "Read-stream drain threw for {RunId}", runId); }

            var duration = (DateTime.UtcNow - info.Execution.StartedAt).TotalSeconds;
            int? exitCode = null;
            try { exitCode = process.ExitCode; } catch { }

            var status = RunStatusClassifier.Classify(exitCode, info.StopReason);
            info.Execution = info.Execution with
            {
                Status = StatusString(status),
                ExitCode = exitCode,
                DurationSeconds = duration,
            };

            // One run-terminal event, 3-valued. A subscriber tells a deliberate stop
            // (Stopped) apart from a crash (Failed) without re-deriving it. Reason fills
            // for Stopped (the stop reason) and Failed (the last turn-failure, else exit
            // code). Either way OnFinished is the canonical "run is done".
            var reason = status switch
            {
                RunOutcome.Stopped => info.StopReason.ToString(),
                RunOutcome.Failed => info.LastFailureReason
                                     ?? (exitCode is int ec ? $"exit code {ec}" : "process ended without a clean exit"),
                _ => null,
            };
            RaiseRunEvent(runId, new CliRunEvent.RunEnded(status, reason, exitCode, duration) { RunId = runId });

            try { OnFinished?.Invoke(runId, info.Execution); }
            catch (Exception ex) { Logger.LogWarning(ex, "OnFinished subscriber threw for {RunId}", runId); }

            // Release the per-run file handles now that streaming is done. The entry
            // stays in _processes so GetExecution/GetOutput keep working post-exit
            // (the persisted log is read statically from disk); call Forget to evict.
            try { info.OutputLog?.Dispose(); } catch { }
            info.CleanContext?.Dispose();
            info.CleanContext = null;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "MonitorProcess threw for {Cli} {RunId}", CliType, runId);
        }
    }

    private static string StatusString(RunOutcome status) => status switch
    {
        RunOutcome.Completed => "completed",
        RunOutcome.Stopped => "stopped",
        _ => "failed",
    };

    // ── Stop / input / read-back ────────────────────────────────────────

    /// <inheritdoc />
    public bool Stop(string runId, RunStopReason reason = RunStopReason.UserStop)
    {
        if (!_processes.TryGetValue(runId, out var info)) return false;
        try
        {
            if (!SafeHasExited(info.Process))
            {
                // Record intent BEFORE the kill so the monitor's classifier can tell a
                // deliberate stop apart from a real crash even if Kill races the exit.
                info.StopReason = reason;
                if (info.KillOverride != null)
                {
                    try { info.KillOverride(reason); }
                    catch (Exception ex) { Logger.LogWarning(ex, "KillOverride threw for {RunId}; falling back to process-tree kill", runId); KillProcessTree(info.Process); }
                }
                else
                {
                    KillProcessTree(info.Process);
                }
                Logger.LogInformation("Killed {Cli} process for {RunId} (reason={Reason})", CliType, runId, reason);
            }
            return true;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Failed to kill {Cli} process for {RunId}", CliType, runId);
            return false;
        }
    }

    /// <inheritdoc />
    public bool SendInput(string runId, string input)
    {
        if (!_processes.TryGetValue(runId, out var info)) return false;
        if (SafeHasExited(info.Process)) return false;
        try
        {
            var bytes = Encoding.UTF8.GetBytes(input + "\n");
            if (info.ChildStdin != null) { info.ChildStdin.Write(bytes, 0, bytes.Length); info.ChildStdin.Flush(); }
            else info.Process.StandardInput.WriteLine(input);
            return true;
        }
        catch { return false; }
    }

    /// <inheritdoc />
    public IReadOnlyList<CliOutputLine> GetOutput(string runId)
    {
        if (_processes.TryGetValue(runId, out var info))
            lock (info.OutputBuffer) return info.OutputBuffer.ToList();
        // Fall back to the persisted log once the in-memory entry is gone.
        return RunLogStore.ReadMerged(LogPaths.GetRunLogDirectory(runId));
    }

    /// <inheritdoc />
    public CliRunInfo? GetExecution(string runId)
        => _processes.TryGetValue(runId, out var info) ? info.Execution : null;

    /// <inheritdoc />
    public virtual bool IsCompatibleSessionId(string? sessionId) => true;

    /// <inheritdoc />
    public bool Forget(string runId)
    {
        if (!_processes.TryRemove(runId, out var info)) return false;
        try { info.OutputLog?.Dispose(); } catch { }
        return true;
    }

    // ── Process-tree kill ───────────────────────────────────────────────

    private void KillProcessTree(Process process)
    {
        int pid;
        try { pid = process.Id; } catch { return; }

        if (OperatingSystem.IsWindows())
        {
            // taskkill /T reaps detached grandchildren that Process.Kill(true) can
            // miss when a child re-parents (Node spawning build tooling, etc).
            try
            {
                using var killer = Process.Start(new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/PID {pid} /T /F",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                killer?.WaitForExit(3000);
                return;
            }
            catch (Exception ex) { Logger.LogDebug(ex, "taskkill failed for PID {Pid}; falling back to Process.Kill", pid); }
        }

        try { process.Kill(entireProcessTree: true); }
        catch (Exception ex) { Logger.LogWarning(ex, "Process.Kill(entireProcessTree) failed for PID {Pid}", pid); }
    }

    private static bool SafeHasExited(Process p)
    {
        try { return p.HasExited; }
        catch { return true; }
    }

    private static int SafePid(Process p)
    {
        try { return p.Id; } catch { return 0; }
    }

    // Per-run mutable tracking state. Private implementation detail.
    private sealed class ProcInfo
    {
        /// <summary>Create tracking state for a freshly spawned run.</summary>
        public ProcInfo(Process process, CliRunInfo execution, string workingDirectory)
        {
            Process = process;
            Execution = execution;
            WorkingDirectory = workingDirectory;
        }

        /// <summary>The spawned process.</summary>
        public Process Process { get; }
        /// <summary>The run info; re-stamped with the terminal status on exit.</summary>
        public CliRunInfo Execution { get; set; }
        /// <summary>The working directory the run spawned in.</summary>
        public string WorkingDirectory { get; }
        /// <summary>The live, capped output buffer (guard with its own lock).</summary>
        public List<CliOutputLine> OutputBuffer { get; } = [];
        /// <summary>The durable per-stream output log.</summary>
        public RunLogStore OutputLog { get; init; } = null!;
        /// <summary>The child's stdin stream, when one was captured.</summary>
        public Stream? ChildStdin { get; init; }
        /// <summary>A PTY driver's own kill action, when present.</summary>
        public Action<RunStopReason>? KillOverride { get; init; }
        /// <summary>The clean-context home to tear down on exit, when used.</summary>
        public CleanContextPreparation? CleanContext { get; set; }
        /// <summary>Why the runner stopped the process; <see cref="RunStopReason.None"/> means it ended on its own.</summary>
        public RunStopReason StopReason { get; set; } = RunStopReason.None;
        /// <summary>Last <see cref="CliRunEvent.TurnFailed"/> reason seen; fills <see cref="CliRunEvent.RunEnded"/>.Reason on a Failed outcome.</summary>
        public string? LastFailureReason { get; set; }
        /// <summary>UTC instant of the last real streamed line (the silence-clock input).</summary>
        public DateTime LastStreamedAt { get; set; }
        /// <summary>The stdout reader loop.</summary>
        public Task? StdoutReadTask { get; set; }
        /// <summary>The stderr reader loop.</summary>
        public Task? StderrReadTask { get; set; }
    }
}

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
using CodingAgentRunner.Quota;

namespace CodingAgentRunner.Execution;

/// <summary>
/// The one spawn engine for every CLI — parameterized by a <see cref="CliDescriptor"/>
/// rather than subclassed. It owns the lifecycle a run goes through (resolve binary
/// &#8594; harden environment &#8594; install the git guard &#8594; spawn &#8594; stream
/// stdout/stderr &#8594; map lines to typed <see cref="CliRunEvent"/>s &#8594; classify
/// the exit) and reads every CLI-specific bit — the argv, the optional stdin payload,
/// the frame parser, the capabilities, the clean-context recipe — from the descriptor.
///
/// <para>
/// <b>internal sealed</b> on purpose: a consumer cannot subclass or even name the
/// engine; it sees only <see cref="ICliDriver"/> / <see cref="ICliCatalog"/> /
/// <see cref="CliDescriptor"/>. Adding a CLI is a descriptor in the catalog, not an
/// override here.
/// </para>
/// </summary>
internal sealed class CliRunEngine : ICliDriver
{
    private readonly CliDescriptor _descriptor;

    // Per-run tracking, keyed by CliRunRequest.RunId.
    private readonly ConcurrentDictionary<string, ProcInfo> _processes = new();
    // Run ids with an active StreamAsync pull-stream — one per run (push events still multiplex).
    private readonly ConcurrentDictionary<string, byte> _streaming = new();
    // A restart after a quota wait appends to the existing log instead of erasing
    // the first attempt's diagnostic output.
    private readonly ConcurrentDictionary<string, byte> _quotaRestarts = new();

    /// <summary>Consumer configuration (paths, git-guard, hardening).</summary>
    private CliOptions Options { get; }

    /// <summary>Diagnostics logger (never null; defaults to a no-op).</summary>
    private ILogger Logger { get; }

    /// <summary>Where per-run output logs are written.</summary>
    private IRunLogPathProvider LogPaths { get; }

    /// <summary>User-home / temp-root provider for clean-context homes.</summary>
    private IUserHomeProvider Home { get; }

    /// <summary>Create the engine for one CLI descriptor with consumer-supplied configuration and providers.</summary>
    public CliRunEngine(
        CliDescriptor descriptor,
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null)
    {
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        Options = options ?? new CliOptions();
        Logger = logger ?? NullLogger.Instance;
        Home = home ?? new DefaultUserHomeProvider();
        LogPaths = logPaths ?? new DefaultRunLogPathProvider(Home);
    }

    /// <inheritdoc />
    public string CliType => _descriptor.CliType;

    /// <inheritdoc />
    public string GetCliPath() => _descriptor.GetCliPath(Options);

    /// <inheritdoc />
    public bool SupportsCleanContext => _descriptor.SupportsCleanContext;

    /// <inheritdoc />
    public CliCapabilities Capabilities(string? model) => _descriptor.Capabilities(model);

    /// <inheritdoc />
    public bool IsCompatibleSessionId(string? sessionId) => _descriptor.CanResumeSessionId(sessionId);

    private static string? NormalizeModel(string? model) => string.IsNullOrWhiteSpace(model) ? null : model.Trim();

    // ── Events ──────────────────────────────────────────────────────────

    /// <inheritdoc />
    public event Action<string, CliOutputLine>? OnOutput;
    /// <inheritdoc />
    public event Action<string, CliRunInfo>? OnStarted;
    /// <inheritdoc />
    public event Action<string, CliRunInfo>? OnFinished;
    /// <inheritdoc />
    public event Action<string, CliRunEvent>? OnRunEvent;

    private void RaiseRunEvent(string runId, CliRunEvent evt)
    {
        try { OnRunEvent?.Invoke(runId, evt); }
        catch (Exception ex) { Logger.LogWarning(ex, "OnRunEvent subscriber threw for {RunId}", runId); }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<CliRunEvent> StreamAsync(
        CliRunRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var runId = request.RunId;

        if (!_streaming.TryAdd(runId, 0))
            throw new InvalidOperationException(
                $"A StreamAsync is already active for run '{runId}'. Use OnRunEvent to fan out to multiple consumers.");

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
            channel.Writer.TryComplete();
            _streaming.TryRemove(runId, out _);
            if (ct.IsCancellationRequested) Stop(runId, RunStopReason.UserStop);
        }
    }

    // ── Availability ────────────────────────────────────────────────────

    /// <inheritdoc />
    public bool IsAvailable() => TestCliPath().Available;

    /// <inheritdoc />
    public (bool Available, string? Version, string Path) TestCliPath(string? path = null)
    {
        // A CLI with a non-standard probe (e.g. Antigravity has no --version) supplies its own.
        if (_descriptor.ProbeCliPath is { } probe) return probe(Options, path);

        var target = string.IsNullOrWhiteSpace(path) ? _descriptor.GetCliPath(Options) : path!;
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

    // ── Test hook ───────────────────────────────────────────────────────

    /// <summary>Test hook: build the launch spec exactly as <see cref="StartAsync"/> would (model + thinking normalized).</summary>
    internal LaunchSpec BuildLaunchForTest(CliRunRequest request)
    {
        var model = NormalizeModel(request.Model);
        var thinking = CliThinkingLevels.Normalize(_descriptor.CliType, model, request.ThinkingLevel);
        return _descriptor.BuildLaunch(new CliLaunchContext(request, _descriptor.GetCliPath(Options), model, thinking, Logger));
    }

    private Task<ChildHandle> SpawnChildAsync(ProcessStartInfo psi)
    {
        // A consumer-injected spawner (e.g. a Windows PTY) takes over the launch; the
        // engine treats its result identically to the built-in pipe spawn.
        if (Options.Spawner is { } spawner)
        {
            var s = spawner.Spawn(psi);
            return Task.FromResult(new ChildHandle(s.Process, s.Stdin, s.Stdout, s.Stderr, s.KillOverride));
        }

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

        if (_descriptor.EnsureHealthy is { } heal)
        {
            var (healthy, healError) = await heal(new PreSpawnHealthContext(() => TestCliPath(), Logger), ct).ConfigureAwait(false);
            if (!healthy)
            {
                Logger.LogError("Pre-spawn health check failed for {Cli} ({RunId}): {Error}", CliType, request.RunId, healError);
                return (null, $"{CliType} CLI not available: {healError}");
            }
        }

        var model = NormalizeModel(request.Model);
        var thinking = CliThinkingLevels.Normalize(CliType, model, request.ThinkingLevel);

        var launch = _descriptor.BuildLaunch(new CliLaunchContext(request, _descriptor.GetCliPath(Options), model, thinking, Logger));

        var psi = new ProcessStartInfo
        {
            FileName = launch.Executable,
            WorkingDirectory = request.WorkingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in launch.Argv) psi.ArgumentList.Add(a);

        var stdinPayload = launch.StdinPayload;
        psi.RedirectStandardInput = !string.IsNullOrEmpty(stdinPayload);

        EnvironmentHardening.Apply(psi, Options.Hardening, Options.EnvironmentOverrides);
        foreach (var kv in launch.EnvironmentOverrides) psi.Environment[kv.Key] = kv.Value;
        if (request.ExtraEnvironment is not null)
            foreach (var kv in request.ExtraEnvironment) psi.Environment[kv.Key] = kv.Value;

        CleanContextPreparation? cleanContext = null;
        if (CliContextModes.Normalize(request.ContextMode) == CliContextModes.Clean && _descriptor.CleanContext is { } cleanSpec)
        {
            cleanContext = CleanContextPreparer.PrepareFromSpec(CliType, cleanSpec, Home.GetUserHome(), Logger);
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
            child = await SpawnChildAsync(psi).ConfigureAwait(false);
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
            CleanContextHome = cleanContext?.TempHome,
        };

        var logDir = LogPaths.GetRunLogDirectory(request.RunId);
        var info = new ProcInfo(process, run, request)
        {
            OutputLog = new RunLogStore(logDir),
            ChildStdin = child.Stdin,
            KillOverride = child.KillOverride,
            CleanContext = cleanContext,
            LastStreamedAt = run.StartedAt,
        };
        if (!_quotaRestarts.TryRemove(request.RunId, out _))
        {
            try { info.OutputLog.Reset(); }
            catch (Exception ex) { Logger.LogWarning(ex, "Failed to reset output log dir {Path}", logDir); }
        }
        _processes[request.RunId] = info;

        try { OnStarted?.Invoke(request.RunId, run); }
        catch (Exception ex) { Logger.LogWarning(ex, "OnStarted subscriber threw for {RunId}", request.RunId); }
        RaiseRunEvent(request.RunId, new CliRunEvent.RunStarted(run.ProcessId, CliType, model) { RunId = request.RunId });

        Logger.LogInformation("Started {Cli} CLI for {RunId} (PID {Pid}) in {Cwd}", CliType, request.RunId, run.ProcessId, request.WorkingDirectory);

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
        var streamKind = CliStreamKinds.Parse(stream);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) break;

                var raw = new CliOutputLine { Timestamp = DateTime.UtcNow, Stream = stream, Text = line };

                var silence = Math.Max(0, (DateTime.UtcNow - info.LastStreamedAt).TotalSeconds);
                info.OutputLog.Append(raw);
                info.LastStreamedAt = DateTime.UtcNow;

                // Interrupt classification — the genuinely new lib behaviour. The library
                // recognises a stop condition in the raw line (env blocker / quota / ...)
                // and emits a typed CliRunEvent.Interrupt. The consumer keeps Stop()
                // authority; the engine never self-stops. Latched once per run so a
                // repeated blocker line yields a single event. Runs on every stream
                // (blockers often surface on stderr).
                if (!info.InterruptLatched)
                {
                    var verdict = _descriptor.InterruptClassifier.Classify(
                        raw.Text, new InterruptContext(runId, info.Phase, silence, _descriptor.CliType));
                    if (verdict is not null && info.TryLatchInterrupt())
                    {
                        var interruptEvt = verdict.ToEvent(runId);
                        info.Phase = RunPhaseTransitions.Apply(info.Phase, interruptEvt);
                        RaiseRunEvent(runId, interruptEvt);
                        if (verdict.Kind == InterruptReason.QuotaExhausted)
                            TryBeginQuotaWait(runId, info, verdict.Detail);
                    }
                }

                // Some adapters intentionally reduce a provider error frame to a
                // short subtype. Inspect the raw frame as well, but only while this
                // opt-in policy is enabled, so disabled behavior remains unchanged.
                if (Options.WaitOnQuota.Enabled
                    && !info.InterruptLatched
                    && QuotaWaitPolicy.IsQuotaLimitFailure(raw.Text)
                    && info.TryLatchInterrupt())
                {
                    const string detail = "CLI reported that its quota limit was reached";
                    RaiseRunEvent(runId, new CliRunEvent.Interrupt(
                        InterruptReason.QuotaExhausted, detail, IsFatal: true) { RunId = runId });
                    TryBeginQuotaWait(runId, info, detail);
                }

                IEnumerable<CliRunEvent>? events = null;
                try { events = _descriptor.Parse(raw.Text, runId, streamKind); }
                catch (Exception ex) { Logger.LogWarning(ex, "descriptor.Parse threw for {RunId}; skipping typed events for this line", runId); }
                if (events != null)
                    foreach (var evt in events)
                    {
                        if (evt is CliRunEvent.TurnFailed tf) info.LastFailureReason = tf.Reason;
                        info.Phase = RunPhaseTransitions.Apply(info.Phase, evt);
                        RaiseRunEvent(runId, evt);
                        if (evt is CliRunEvent.TurnFailed failed
                            && Options.WaitOnQuota.Enabled
                            && QuotaWaitPolicy.IsQuotaLimitFailure(failed.Reason)
                            && info.TryLatchInterrupt())
                        {
                            RaiseRunEvent(runId, new CliRunEvent.Interrupt(
                                InterruptReason.QuotaExhausted, failed.Reason, IsFatal: true) { RunId = runId });
                            TryBeginQuotaWait(runId, info, failed.Reason);
                        }
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

    private void TryBeginQuotaWait(string runId, ProcInfo info, string detail)
    {
        if (!Options.WaitOnQuota.Enabled || Options.WaitOnQuota.QuotaService is null)
            return;
        info.TrySetQuotaWaitTask(() => EvaluateQuotaWaitAsync(runId, info, detail));
    }

    private async Task<DateTime?> EvaluateQuotaWaitAsync(string runId, ProcInfo info, string detail)
    {
        QuotaSnapshot? snapshot;
        try
        {
            snapshot = await Options.WaitOnQuota.QuotaService!
                .RefreshAsync(CliType, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "QuotaWaitProbeFailed for {Cli} {RunId}", CliType, runId);
            return null;
        }

        var now = Options.WaitOnQuota.TimeProvider.GetUtcNow().UtcDateTime;
        var resetAt = QuotaWaitPolicy.SelectReset(snapshot, Options.WaitOnQuota, now);
        if (resetAt is null) return null;

        info.Execution = info.Execution with { Status = "waiting" };
        RaiseRunEvent(runId, new CliRunEvent.QuotaWaitStarted(detail, resetAt.Value) { RunId = runId });
        Logger.LogInformation(
            "QuotaWaitStarted for {Cli} {RunId}; reset at {ResetAt} (delay {DelaySeconds:F0}s)",
            CliType, runId, resetAt.Value, (resetAt.Value - now).TotalSeconds);
        Stop(runId, RunStopReason.QuotaResetWait);
        return resetAt;
    }

    private async Task MonitorProcessAsync(string runId, Process process, ProcInfo info, CancellationToken ct)
    {
        try
        {
            try { await process.WaitForExitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { Stop(runId, RunStopReason.Cancelled); }

            try
            {
                await Task.WhenAll(info.StdoutReadTask ?? Task.CompletedTask, info.StderrReadTask ?? Task.CompletedTask)
                    .WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            }
            catch (TimeoutException) { Logger.LogWarning("Read-stream drain timed out for {RunId}; some output may be missing", runId); }
            catch (Exception ex) { Logger.LogDebug(ex, "Read-stream drain threw for {RunId}", runId); }

            var resetAt = info.QuotaWaitTask is null
                ? null
                : await info.QuotaWaitTask.ConfigureAwait(false);
            if (resetAt is not null)
            {
                var delay = resetAt.Value - Options.WaitOnQuota.TimeProvider.GetUtcNow().UtcDateTime;
                try
                {
                    if (delay > TimeSpan.Zero)
                    {
                        if (Options.WaitOnQuota.DelayAsync is { } delayAsync)
                            await delayAsync(delay, ct).ConfigureAwait(false);
                        else
                            await Task.Delay(delay, Options.WaitOnQuota.TimeProvider, ct).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    info.Execution = info.Execution with { Status = "stopped" };
                    RaiseRunEvent(runId, new CliRunEvent.RunEnded(
                        RunOutcome.Stopped, RunStopReason.Cancelled.ToString(), null, 0) { RunId = runId });
                    try { OnFinished?.Invoke(runId, info.Execution); } catch { }
                    try { info.OutputLog?.Dispose(); } catch { }
                    info.CleanContext?.Dispose();
                    info.CleanContext = null;
                    return;
                }

                try { info.OutputLog?.Dispose(); } catch { }
                info.CleanContext?.Dispose();
                info.CleanContext = null;
                _processes.TryRemove(runId, out _);
                RaiseRunEvent(runId, new CliRunEvent.QuotaWaitEnded(resetAt.Value) { RunId = runId });
                Logger.LogInformation("QuotaWaitEnded for {Cli} {RunId}; restarting the request", CliType, runId);
                _quotaRestarts[runId] = 0;
                var (_, restartError) = await StartAsync(info.Request, ct).ConfigureAwait(false);
                if (restartError is null) return;

                _quotaRestarts.TryRemove(runId, out _);
                Logger.LogError("QuotaWaitRestartFailed for {Cli} {RunId}: {Error}", CliType, runId, restartError);
                RaiseRunEvent(runId, new CliRunEvent.RunEnded(
                    RunOutcome.Failed, restartError, null, 0) { RunId = runId });
                info.Execution = info.Execution with { Status = "failed" };
                try { OnFinished?.Invoke(runId, info.Execution); } catch { }
                return;
            }

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
        return RunLogStore.ReadMerged(LogPaths.GetRunLogDirectory(runId));
    }

    /// <inheritdoc />
    public CliRunInfo? GetExecution(string runId)
        => _processes.TryGetValue(runId, out var info) ? info.Execution : null;

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
        public ProcInfo(Process process, CliRunInfo execution, CliRunRequest request)
        {
            Process = process;
            Execution = execution;
            Request = request;
            WorkingDirectory = request.WorkingDirectory;
        }

        public Process Process { get; }
        public CliRunInfo Execution { get; set; }
        public string WorkingDirectory { get; }
        public CliRunRequest Request { get; }
        public List<CliOutputLine> OutputBuffer { get; } = [];
        public RunLogStore OutputLog { get; init; } = null!;
        public Stream? ChildStdin { get; init; }
        public Action<RunStopReason>? KillOverride { get; init; }
        public CleanContextPreparation? CleanContext { get; set; }
        public RunStopReason StopReason { get; set; } = RunStopReason.None;
        public string? LastFailureReason { get; set; }
        public DateTime LastStreamedAt { get; set; }
        public Task? StdoutReadTask { get; set; }
        public Task? StderrReadTask { get; set; }
        public Task<DateTime?>? QuotaWaitTask { get; private set; }

        // Advisory run phase fed to the interrupt classifier's context. Updated from
        // both reader threads; an enum write is atomic and a lost update only yields a
        // slightly-stale advisory phase, never a torn value.
        public RunPhase Phase { get; set; } = RunPhase.Spawning;

        // Emit at most one Interrupt per run, even though stdout + stderr read on
        // separate threads. Interlocked makes the latch race-free.
        private int _interruptLatched;
        public bool InterruptLatched => Volatile.Read(ref _interruptLatched) == 1;
        public bool TryLatchInterrupt() => Interlocked.CompareExchange(ref _interruptLatched, 1, 0) == 0;

        public void TrySetQuotaWaitTask(Func<Task<DateTime?>> start)
        {
            lock (this)
                QuotaWaitTask ??= start();
        }
    }
}

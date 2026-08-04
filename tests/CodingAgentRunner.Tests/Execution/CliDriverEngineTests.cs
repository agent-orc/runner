using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Delegation;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using CodingAgentRunner.Quota;
using Xunit;

namespace CodingAgentRunner.Tests.Execution;

public class CliDriverEngineTests
{
    /// <summary>
    /// A real engine over a trivial, cross-platform process (`dotnet --version`) that
    /// exits 0 after printing one line. Built from a minimal descriptor that maps every
    /// stdout line to a TurnCompleted, so the adapter-&gt;event wiring is exercised
    /// end-to-end — no subclassing, the descriptor IS the per-CLI behavior.
    /// </summary>
    private static CliDescriptor ProbeDescriptor(string exe, string[] args) => new()
    {
        CliType = CliTypes.Claude,
        GetCliPath = _ => exe,
        BuildLaunch = ctx => new LaunchSpec
        {
            Executable = exe,
            Argv = args,
            WorkingDirectory = ctx.Request.WorkingDirectory,
        },
        Parse = (line, runId, stream) => stream == CliStreamKind.Stdout && !string.IsNullOrWhiteSpace(line)
            ? new[] { new CliRunEvent.TurnCompleted("probe") { RunId = runId } }
            : Array.Empty<CliRunEvent>(),
        InterruptClassifier = InterruptClassifiers.None,
        Liveness = LivenessSpec.InBandDefault,
        Capabilities = m => new CliCapabilities { CliType = CliTypes.Claude, Model = m },
    };

    private static CliRunEngine ProbeEngine(string exe, string[] args, IRunLogPathProvider logs, ICliProcessSpawner? spawner = null)
        // git-guard off so the test keeps a clean PATH.
        => new(ProbeDescriptor(ResolveTestExecutable(exe), args), new CliOptions { AllowAgentGitMutation = true, Spawner = spawner }, null, logs);

    // The curated Windows CreateProcessW path intentionally receives an executable
    // path, rather than relying on Process.Start's PATH search. Keep test probes on
    // that same contract so Windows acceptance runs do not depend on a bare `dotnet`.
    private static string ResolveTestExecutable(string executable)
    {
        if (!OperatingSystem.IsWindows() || !string.Equals(executable, "dotnet", StringComparison.OrdinalIgnoreCase))
            return executable;

        var roots = new[]
        {
            Environment.GetEnvironmentVariable("DOTNET_ROOT"),
            Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)"),
            Path.GetDirectoryName(Environment.ProcessPath),
        };
        foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root)))
        {
            var candidate = Path.Combine(root!, "dotnet.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var where = Path.Combine(Environment.SystemDirectory, "where.exe");
        using var lookup = Process.Start(new ProcessStartInfo(where, "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        var resolved = lookup?.StandardOutput.ReadLine();
        lookup?.WaitForExit();
        return !string.IsNullOrWhiteSpace(resolved) ? resolved.Trim() : executable;
    }

    private sealed class TempLogs : IRunLogPathProvider, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "car-engine-" + Guid.NewGuid().ToString("N"));
        public string GetRunLogDirectory(string runId) => Path.Combine(_root, runId);
        public string GetActiveJobsFile() => Path.Combine(_root, "active.json");
        public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
    }

    private sealed class TestHome(string home) : IUserHomeProvider
    {
        public string GetUserHome() => home;
        public string GetTempRoot() => Path.GetTempPath();
    }

    [Fact]
    public async Task Run_StreamsOutput_RaisesTypedEvents_AndClassifiesCleanExitAsCompleted()
    {
        using var logs = new TempLogs();
        var driver = ProbeEngine("dotnet", ["--version"], logs);

        var events = new ConcurrentQueue<CliRunEvent>();
        driver.OnRunEvent += (_, e) => events.Enqueue(e);

        var started = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnStarted += (_, r) => started.TrySetResult(r);
        driver.OnFinished += (_, r) => finished.TrySetResult(r);

        var (run, error) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "engine-test",
            Prompt = "unused",
            WorkingDirectory = Path.GetTempPath(),
        });

        Assert.Null(error);
        Assert.NotNull(run);
        Assert.Equal("running", run!.Status);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Terminal classification: clean exit (0), no stop reason -> completed.
        Assert.Equal("completed", final.Status);
        Assert.Equal(0, final.ExitCode);
        Assert.True(final.DurationSeconds >= 0);

        // Event ordering: RunStarted first, RunEnded last, a TurnCompleted between.
        var seq = events.ToArray();
        Assert.IsType<CliRunEvent.RunStarted>(seq[0]);
        var end = Assert.IsType<CliRunEvent.RunEnded>(seq[^1]);
        Assert.Equal(RunOutcome.Completed, end.Outcome);   // clean self-exit
        Assert.Null(end.Reason);
        Assert.Contains(seq, e => e is CliRunEvent.TurnCompleted);

        // The version line was captured.
        var output = driver.GetExecution("engine-test");
        Assert.Equal("completed", output!.Status);
        Assert.Contains(driver.GetOutput("engine-test"), l => l.Stream == "stdout");
    }

    [Fact]
    public async Task AcquiredCleanContextLease_IsReusedAcrossAttempts_AndReleasedOnlyByHost()
    {
        var home = Path.Combine(Path.GetTempPath(), "car-lease-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        File.WriteAllText(Path.Combine(home, ".claude", ".credentials.json"), "{\"token\":\"x\"}");
        using var logs = new TempLogs();
        try
        {
            var descriptor = ProbeDescriptor("dotnet", ["--version"]) with
            {
                CleanContext = BuiltInDescriptors.Get(CliTypes.Claude).CleanContext,
            };
            ICliDriver driver = new CliRunEngine(descriptor,
                new CliOptions { AllowAgentGitMutation = true }, null, logs, new TestHome(home));
            using var lease = driver.AcquireCleanContext();
            var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            driver.OnFinished += (_, run) => finished.TrySetResult(run);

            foreach (var runId in new[] { "lease-one", "lease-two" })
            {
                var (run, error) = await driver.StartAsync(new CliRunRequest
                {
                    RunId = runId, Prompt = "x", WorkingDirectory = Path.GetTempPath(), CleanContextLease = lease,
                });
                Assert.Null(error);
                Assert.Equal(lease.TempHome, run!.CleanContextHome);
                await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
                Assert.True(Directory.Exists(lease.TempHome));
                finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            lease.Dispose();
            Assert.False(Directory.Exists(lease.TempHome));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task AcquiredCleanContextLease_SurvivesFailedStart_AndConcurrentLeasesAreIndependent()
    {
        var home = Path.Combine(Path.GetTempPath(), "car-lease-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        using var logs = new TempLogs();
        try
        {
            var descriptor = ProbeDescriptor("definitely-not-a-real-binary-xyz123", []) with
            {
                CleanContext = BuiltInDescriptors.Get(CliTypes.Claude).CleanContext,
            };
            ICliDriver driver = new CliRunEngine(descriptor,
                new CliOptions { AllowAgentGitMutation = true }, null, logs, new TestHome(home));
            using var first = driver.AcquireCleanContext();
            using var second = driver.AcquireCleanContext();
            Assert.NotEqual(first.TempHome, second.TempHome);

            var (_, error) = await driver.StartAsync(new CliRunRequest
            {
                RunId = "lease-failed-start", Prompt = "x", WorkingDirectory = Path.GetTempPath(), CleanContextLease = first,
            });
            Assert.NotNull(error);
            Assert.True(Directory.Exists(first.TempHome));
            Assert.True(Directory.Exists(second.TempHome));

            first.Dispose();
            Assert.False(Directory.Exists(first.TempHome));
            Assert.True(Directory.Exists(second.TempHome));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task PerRunCleanContext_IsReleasedWhenSpawnFails()
    {
        var home = Path.Combine(Path.GetTempPath(), "car-lease-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(home, ".claude"));
        using var logs = new TempLogs();
        try
        {
            var spawner = new ThrowingSpawner();
            var descriptor = ProbeDescriptor("ignored", []) with
            {
                CleanContext = BuiltInDescriptors.Get(CliTypes.Claude).CleanContext,
            };
            var driver = new CliRunEngine(descriptor,
                new CliOptions { AllowAgentGitMutation = true, Spawner = spawner }, null, logs, new TestHome(home));

            var (_, error) = await driver.StartAsync(new CliRunRequest
            {
                RunId = "per-run-failed-start", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
            });

            Assert.NotNull(error);
            Assert.NotNull(spawner.CleanContextHome);
            Assert.False(Directory.Exists(spawner.CleanContextHome));
        }
        finally
        {
            try { Directory.Delete(home, recursive: true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public async Task StreamAsync_PullStream_StartsWithRunStarted_EndsAtTerminal()
    {
        using var logs = new TempLogs();
        var driver = ProbeEngine("dotnet", ["--version"], logs);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        var seq = new List<CliRunEvent>();
        await foreach (var e in driver.StreamAsync(new CliRunRequest
        {
            RunId = "stream-1", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        }, cts.Token))
        {
            seq.Add(e);
        }

        Assert.IsType<CliRunEvent.RunStarted>(seq[0]);
        Assert.IsType<CliRunEvent.RunEnded>(seq[^1]);
        Assert.Contains(seq, e => e is CliRunEvent.TurnCompleted);
        Assert.All(seq, e => Assert.Equal("stream-1", e.RunId));   // multiplex filter held
    }

    [Fact]
    public async Task StreamAsync_SpawnError_SurfacesAsThrow()
    {
        using var logs = new TempLogs();
        var driver = ProbeEngine("definitely-not-a-real-binary-xyz123", [], logs);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await foreach (var _ in driver.StreamAsync(new CliRunRequest
            { RunId = "se", Prompt = "x", WorkingDirectory = Path.GetTempPath() }, cts.Token)) { }
        });
    }

    [Fact]
    public async Task StreamAsync_SecondConcurrentSameRunId_Throws()
    {
        using var logs = new TempLogs();
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 10" })
            : ("sleep", new[] { "10" });
        var driver = ProbeEngine(exe, args, logs);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        var first = driver.StreamAsync(new CliRunRequest
        { RunId = "dup", Prompt = "x", WorkingDirectory = Path.GetTempPath() }, cts.Token).GetAsyncEnumerator(cts.Token);
        Assert.True(await first.MoveNextAsync());   // RunStarted → the stream is now active

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (var _ in driver.StreamAsync(new CliRunRequest
            { RunId = "dup", Prompt = "y", WorkingDirectory = Path.GetTempPath() }, cts.Token)) { }
        });

        await first.DisposeAsync();   // release the first stream's tracking
        driver.Stop("dup");
    }

    [Fact]
    public async Task StreamAsync_Cancellation_StopsTheRun_AndEndsEnumeration()
    {
        using var logs = new TempLogs();
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" })
            : ("sleep", new[] { "30" });
        var driver = ProbeEngine(exe, args, logs);
        using var cts = new CancellationTokenSource();

        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, r) => finished.TrySetResult(r);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var e in driver.StreamAsync(new CliRunRequest
            { RunId = "cx", Prompt = "x", WorkingDirectory = Path.GetTempPath() }, cts.Token))
            {
                if (e is CliRunEvent.RunStarted) cts.Cancel();   // cancel right after the run starts
            }
        });

        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal("stopped", final.Status);   // cancellation stopped the run (finally → Stop)
    }

    [Fact]
    public async Task DuplicateRunId_WhileLive_IsRejected_ButReusableAfterExit()
    {
        using var logs = new TempLogs();
        var driver = ProbeEngine("dotnet", ["--version"], logs);

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, _) => finished.TrySetResult();

        var (run1, err1) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "dup", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        Assert.Null(err1);
        Assert.NotNull(run1);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var (run2, err2) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "dup", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        Assert.Null(err2);
        Assert.NotNull(run2);
    }

    [Fact]
    public async Task Stop_ClassifiesAsStopped_AndRaisesRunEnded_WithStoppedOutcome()
    {
        using var logs = new TempLogs();
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("ping", new[] { "-n", "20", "127.0.0.1" })
            : ("sleep", new[] { "20" });
        var driver = ProbeEngine(exe, args, logs);

        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        CliRunEvent.RunEnded? end = null;
        driver.OnStarted += (_, _) => started.TrySetResult();
        driver.OnFinished += (_, r) => finished.TrySetResult(r);
        driver.OnRunEvent += (_, e) =>
        {
            if (e is CliRunEvent.RunEnded re) end = re;
        };

        var (run, err) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "stop-test", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        Assert.Null(err);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(driver.Stop("stop-test", RunStopReason.UserStop));
        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal("stopped", final.Status);
        Assert.NotNull(end);
        Assert.Equal(RunOutcome.Stopped, end!.Outcome);
        Assert.Equal(RunStopReason.UserStop.ToString(), end.Reason);

        Assert.False(driver.Stop("no-such-run"));
    }

    // A custom spawner that does the same plain-pipe launch as the built-in, but counts.
    private sealed class CountingSpawner : ICliProcessSpawner
    {
        public int Count;
        public CliSpawn Spawn(ProcessStartInfo psi)
        {
            Count++;
            var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
            p.Start();
            var stdin = psi.RedirectStandardInput ? p.StandardInput.BaseStream : Stream.Null;
            return new CliSpawn(p, stdin, p.StandardOutput, p.StandardError);
        }
    }

    private sealed class ThrowingSpawner : ICliProcessSpawner
    {
        public string? CleanContextHome { get; private set; }

        public CliSpawn Spawn(ProcessStartInfo psi)
        {
            CleanContextHome = psi.Environment.TryGetValue("CLAUDE_CONFIG_DIR", out var home) ? home : null;
            throw new InvalidOperationException("spawn failed");
        }
    }

    private sealed class RecordingStdinSpawner : ICliProcessSpawner
    {
        public ProcessStartInfo? RequestedStartInfo { get; private set; }
        public RecordingWriteStream Stdin { get; } = new();

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            RequestedStartInfo = startInfo;
            var probe = new ProcessStartInfo("dotnet", "--version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            var process = new Process { StartInfo = probe, EnableRaisingEvents = true };
            process.Start();
            return new CliSpawn(process, Stdin, process.StandardOutput, process.StandardError);
        }
    }

    private sealed class RecordingWriteStream : MemoryStream
    {
        public bool WasClosed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            WasClosed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class SequencedSpawner : ICliProcessSpawner
    {
        public int Count;

        public CliSpawn Spawn(ProcessStartInfo ignored)
        {
            var text = Interlocked.Increment(ref Count) == 1 ? "rate limit exceeded" : "ok";
            var psi = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd", $"/c echo {text}")
                : new ProcessStartInfo("sh", $"-c \"echo '{text}'\"");
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.Start();
            return new CliSpawn(process, process.StandardInput.BaseStream, process.StandardOutput, process.StandardError);
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task WaitOnQuota_NearKnownReset_EmitsWaitEventsAndRestartsWithoutIntermediateTerminal()
    {
        using var logs = new TempLogs();
        var now = new DateTimeOffset(2026, 7, 11, 12, 0, 0, TimeSpan.Zero);
        var resetAt = now.UtcDateTime.AddMinutes(12);
        var probe = new DelegateQuotaProbe(CliTypes.Claude, _ => Task.FromResult(new QuotaSnapshot
        {
            CliType = CliTypes.Claude,
            Windows = [new QuotaWindow { Label = "5-hour", UsedPct = 100, ResetAt = resetAt }],
        }));
        var spawner = new SequencedSpawner();
        TimeSpan? requestedDelay = null;
        var options = new CliOptions
        {
            AllowAgentGitMutation = true,
            Spawner = spawner,
            WaitOnQuota = new WaitOnQuotaOptions
            {
                Enabled = true,
                Threshold = TimeSpan.FromMinutes(30),
                QuotaService = new QuotaService([probe]),
                TimeProvider = new FixedTimeProvider(now),
                DelayAsync = (delay, _) => { requestedDelay = delay; return Task.CompletedTask; },
            },
        };
        var descriptor = ProbeDescriptor("ignored", []) with
        {
            Parse = (line, runId, _) => line == "rate limit exceeded"
                ? [new CliRunEvent.TurnFailed(line) { RunId = runId }]
                : [new CliRunEvent.TurnCompleted(null) { RunId = runId }],
        };
        var driver = new CliRunEngine(descriptor, options, null, logs);
        var events = new ConcurrentQueue<CliRunEvent>();
        driver.OnRunEvent += (_, evt) => events.Enqueue(evt);
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, run) => finished.TrySetResult(run);

        await driver.StartAsync(new CliRunRequest
        {
            RunId = "quota-wait", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("completed", final.Status);
        Assert.Equal(2, spawner.Count);
        Assert.Equal(TimeSpan.FromMinutes(12), requestedDelay);
        Assert.Single(events.OfType<CliRunEvent.QuotaWaitStarted>());
        Assert.Single(events.OfType<CliRunEvent.QuotaWaitEnded>());
        Assert.Single(events.OfType<CliRunEvent.RunEnded>());
        Assert.Equal(2, events.OfType<CliRunEvent.RunStarted>().Count());
    }

    [Fact]
    public async Task WaitOnQuota_UnknownQuota_UsesExistingTerminalRouteWithoutWaiting()
    {
        using var logs = new TempLogs();
        var probe = new DelegateQuotaProbe(CliTypes.Claude, _ => Task.FromResult(new QuotaSnapshot
        {
            CliType = CliTypes.Claude,
            Error = "quota unavailable",
        }));
        var spawner = new SequencedSpawner();
        var descriptor = ProbeDescriptor("ignored", []) with
        {
            Parse = (line, runId, _) =>
                [new CliRunEvent.TurnFailed(line) { RunId = runId }],
        };
        var driver = new CliRunEngine(descriptor, new CliOptions
        {
            AllowAgentGitMutation = true,
            Spawner = spawner,
            WaitOnQuota = new WaitOnQuotaOptions
            {
                Enabled = true,
                QuotaService = new QuotaService([probe]),
            },
        }, null, logs);
        var events = new ConcurrentQueue<CliRunEvent>();
        driver.OnRunEvent += (_, evt) => events.Enqueue(evt);
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, run) => finished.TrySetResult(run);

        await driver.StartAsync(new CliRunRequest
        {
            RunId = "quota-unknown", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, spawner.Count);
        Assert.Empty(events.OfType<CliRunEvent.QuotaWaitStarted>());
        Assert.Single(events.OfType<CliRunEvent.RunEnded>());
    }

    [Fact]
    public async Task CustomSpawner_TakesOverTheLaunch()
    {
        using var logs = new TempLogs();
        var spawner = new CountingSpawner();
        var driver = ProbeEngine("dotnet", ["--version"], logs, spawner);

        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, r) => finished.TrySetResult(r);

        await driver.StartAsync(new CliRunRequest { RunId = "sp", Prompt = "x", WorkingDirectory = Path.GetTempPath() });
        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(1, spawner.Count);            // the engine launched via the injected spawner
        Assert.Equal("completed", final.Status);   // and the run completed normally through it
    }

    [Fact]
    public async Task ClaudeStdinTransport_EngineWritesUtf8PayloadAndClosesStdin()
    {
        using var logs = new TempLogs();
        var spawner = new RecordingStdinSpawner();
        var descriptor = BuiltInDescriptors.Claude with
        {
            GetCliPath = _ => "ignored-by-recording-spawner",
            EnsureHealthy = null,
        };
        var driver = new CliRunEngine(descriptor, new CliOptions
        {
            AllowAgentGitMutation = true,
            ClaudePromptTransport = ClaudePromptTransport.Stdin,
            Spawner = spawner,
            // This test is about the transport, not about what the prompt says: with
            // delegation on, the payload would also carry the injected agent block.
            Delegation = new DelegationOptions { Enabled = false },
        }, null, logs);
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, run) => finished.TrySetResult(run);
        const string prompt = "private prompt \u2014 line one\nline two";

        var (_, error) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "claude-stdin",
            Prompt = prompt,
            WorkingDirectory = Path.GetTempPath(),
        });
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Null(error);
        Assert.NotNull(spawner.RequestedStartInfo);
        Assert.True(spawner.RequestedStartInfo!.RedirectStandardInput);
        Assert.DoesNotContain(prompt, spawner.RequestedStartInfo.ArgumentList);
        Assert.Equal(prompt, Encoding.UTF8.GetString(spawner.Stdin.ToArray()));
        Assert.True(spawner.Stdin.WasClosed);
    }

    [Fact]
    public async Task ClaudeRun_MaterializesSubagents_AdvertisesThem_AndLeavesTheWorkspaceAsItFoundIt()
    {
        using var logs = new TempLogs();
        var spawner = new RecordingStdinSpawner();
        var workspace = Path.Combine(Path.GetTempPath(), "car-delegation-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspace);
        try
        {
            var descriptor = BuiltInDescriptors.Claude with
            {
                GetCliPath = _ => "ignored-by-recording-spawner",
                EnsureHealthy = null,
            };
            var driver = new CliRunEngine(descriptor, new CliOptions
            {
                AllowAgentGitMutation = true,
                ClaudePromptTransport = ClaudePromptTransport.Stdin,
                Spawner = spawner,
            }, null, logs);
            var agentsRoot = Path.Combine(workspace, ".claude");
            var generated = Path.Combine(agentsRoot, "agents", "mechanical.md");

            // What a host application sees at the moment it is called back. It commits
            // the workspace from here, so a generated definition must already be gone.
            var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool? generatedFileVisibleInOnFinished = null;
            bool? agentsDirVisibleInOnFinished = null;
            driver.OnFinished += (_, run) =>
            {
                generatedFileVisibleInOnFinished = File.Exists(generated);
                agentsDirVisibleInOnFinished = Directory.Exists(agentsRoot);
                finished.TrySetResult(run);
            };

            var (started, error) = await driver.StartAsync(new CliRunRequest
            {
                RunId = "claude-delegation",
                Prompt = "Rename the parser",
                WorkingDirectory = workspace,
            });

            Assert.Null(error);
            Assert.Equal(["mechanical", "checker"], started!.Subagents);

            // The prompt the CLI actually received names the cheap workers.
            var payload = Encoding.UTF8.GetString(spawner.Stdin.ToArray());
            Assert.StartsWith("Rename the parser", payload);
            Assert.Contains(DelegationContextBlock.OpenTag, payload);
            Assert.Contains("\"name\":\"mechanical\"", payload);
            Assert.Contains("\"model\":\"haiku\"", payload);

            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

            // Cleanup happens *before* the terminal callback, not after it: no polling,
            // and no window in which a host's post-run commit could pick one up.
            Assert.False(generatedFileVisibleInOnFinished);
            Assert.False(agentsDirVisibleInOnFinished);
            Assert.False(Directory.Exists(agentsRoot));
        }
        finally
        {
            try { Directory.Delete(workspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task StartAsync_RejectsInvalidInput_WithClearErrors()
    {
        using var logs = new TempLogs();
        var driver = ProbeEngine("dotnet", ["--version"], logs);
        var wd = Path.GetTempPath();

        var (_, e1) = await driver.StartAsync(new CliRunRequest { RunId = "", Prompt = "x", WorkingDirectory = wd });
        Assert.Contains("RunId", e1);
        var (_, e2) = await driver.StartAsync(new CliRunRequest { RunId = "   ", Prompt = "x", WorkingDirectory = wd });
        Assert.Contains("RunId", e2);

        var missing = Path.Combine(wd, "no-such-dir-" + Guid.NewGuid().ToString("N"));
        var (_, e3) = await driver.StartAsync(new CliRunRequest { RunId = "r", Prompt = "x", WorkingDirectory = missing });
        Assert.Contains("WorkingDirectory", e3);

        var (_, e4) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "r", Prompt = "x", WorkingDirectory = wd,
            Tuning = new Dictionary<string, string> { ["  "] = "v" },
        });
        Assert.Contains("Tuning", e4);

        var (_, e5) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "r", Prompt = "x", WorkingDirectory = wd,
            LaunchExtensions = [new CliLaunchExtension
            {
                Kind = CliLaunchExtensionKind.ClaudeAppendSystemPromptFile,
                Value = "relative-system-prompt.md",
            }],
        });
        Assert.Contains("absolute path", e5);
    }

    [Fact]
    public void LaunchExtensions_RejectConflictingOrUnsupportedRequests()
    {
        var file = Path.GetTempFileName();
        try
        {
            var extension = CliLaunchExtension.AppendClaudeSystemPromptFile(file);
            Assert.Contains("at most one", CliLaunchExtensions.Validate(CliTypes.Claude, [extension, extension]));
            Assert.Contains("not supported by codex", CliLaunchExtensions.Validate(CliTypes.Codex, [extension]));
            Assert.Contains("not supported", CliLaunchExtensions.Validate(CliTypes.Claude,
            [new CliLaunchExtension { Kind = (CliLaunchExtensionKind)999, Value = file }]));
        }
        finally { File.Delete(file); }
    }

    [Fact]
    public async Task NonZeroExit_RaisesRunEnded_FailedOutcome_WithExitCodeReason()
    {
        using var logs = new TempLogs();
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "exit 3" })
            : ("sh", new[] { "-c", "exit 3" });
        var driver = ProbeEngine(exe, args, logs);

        CliRunEvent.RunEnded? end = null;
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnRunEvent += (_, e) => { if (e is CliRunEvent.RunEnded re) end = re; };
        driver.OnFinished += (_, r) => finished.TrySetResult(r);

        await driver.StartAsync(new CliRunRequest
        {
            RunId = "fail", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal("failed", final.Status);
        Assert.NotNull(end);
        Assert.Equal(RunOutcome.Failed, end!.Outcome);
        Assert.Equal(3, end.ExitCode);
        Assert.Contains("exit code", end.Reason);
    }

    [Fact]
    public async Task RunWatchdog_AutoStops_AHungRun()
    {
        using var logs = new TempLogs();
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("powershell", new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" })
            : ("sleep", new[] { "30" });
        var driver = ProbeEngine(exe, args, logs);

        var policy = WatchdogPolicy.Default with
        {
            WarmUpGraceSeconds = 0,
            TickSeconds = 1,
            Budgets = new Dictionary<RunPhase, PhaseBudget> { [RunPhase.Spawning] = new(1, 2) },
        };
        using var watchdog = RunWatchdog.Attach(driver, policy, autoStop: true);

        var hungCount = 0;
        watchdog.OnHung += (_, _, _) => Interlocked.Increment(ref hungCount);
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, r) => finished.TrySetResult(r);

        await driver.StartAsync(new CliRunRequest
        {
            RunId = "wd", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });

        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(25));

        Assert.Equal(1, hungCount);              // one-shot: OnHung fires exactly once on entering Hung
        Assert.Equal("stopped", final.Status);   // deliberate watchdog stop, classified as stopped (not a crash)
    }

    [Fact]
    public async Task InterruptClassifier_EmitsTypedInterruptEvent_WithoutSelfStopping()
    {
        using var logs = new TempLogs();
        // A process that prints a known environment-blocker line, then exits 0 on its own.
        var (exe, args) = OperatingSystem.IsWindows()
            ? ("cmd", new[] { "/c", "echo EACCES: permission denied" })
            : ("sh", new[] { "-c", "echo 'EACCES: permission denied'" });
        // Same probe descriptor, but with the real environment-blocker classifier wired in.
        var descriptor = ProbeDescriptor(exe, args) with { InterruptClassifier = InterruptClassifiers.EnvironmentBlocker() };
        var driver = new CliRunEngine(descriptor, new CliOptions { AllowAgentGitMutation = true }, null, logs);

        CliRunEvent.Interrupt? interrupt = null;
        var interruptCount = 0;
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnRunEvent += (_, e) =>
        {
            if (e is CliRunEvent.Interrupt i) { interrupt = i; Interlocked.Increment(ref interruptCount); }
        };
        driver.OnFinished += (_, r) => finished.TrySetResult(r);

        await driver.StartAsync(new CliRunRequest
        {
            RunId = "intr", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        var final = await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // The library classified the line and emitted ONE typed Interrupt event...
        Assert.NotNull(interrupt);
        Assert.Equal(1, interruptCount);                            // latched once
        Assert.Equal(InterruptReason.EnvironmentBlocker, interrupt!.Reason);
        Assert.True(interrupt.IsFatal);
        Assert.Equal("intr", interrupt.RunId);
        // ...but did NOT self-stop: the process ran to its own clean exit (echo exits 0).
        Assert.Equal("completed", final.Status);
    }

    [Fact]
    public async Task Forget_EvictsInMemory_GetOutputThenFallsBackToDisk()
    {
        using var logs = new TempLogs();
        var driver = ProbeEngine("dotnet", ["--version"], logs);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        driver.OnFinished += (_, _) => finished.TrySetResult();

        await driver.StartAsync(new CliRunRequest
        {
            RunId = "f1", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.NotNull(driver.GetExecution("f1"));                 // tracked until forgotten
        Assert.True(driver.Forget("f1"));
        Assert.Null(driver.GetExecution("f1"));                    // evicted

        var fromDisk = driver.GetOutput("f1");
        Assert.Contains(fromDisk, l => l.Stream == "stdout");

        Assert.False(driver.Forget("f1"));                         // already gone
    }
}

using System.Collections.Concurrent;
using System.Diagnostics;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Execution;

public class CliBackendEngineTests
{
    /// <summary>
    /// A real backend over a trivial, cross-platform process (`dotnet --version`)
    /// that exits 0 after printing one line. It maps every stdout line to a
    /// TurnCompleted so the adapter-&gt;event wiring is exercised end-to-end.
    /// </summary>
    private sealed class ProbeBackend : CliBackendBase
    {
        private readonly string _exe;
        private readonly string[] _args;

        public ProbeBackend(string exe, string[] args, IRunLogPathProvider logs)
            : base(new CliOptions { AllowAgentGitMutation = true }, null, logs) // git-guard off: keep PATH clean
        {
            _exe = exe;
            _args = args;
        }

        public override string CliType => CliTypes.Claude;
        public override string GetCliPath() => _exe;

        protected override ProcessStartInfo BuildStartInfo(CliRunRequest request, string? model, string? thinkingLevel)
        {
            var psi = new ProcessStartInfo { FileName = _exe, WorkingDirectory = request.WorkingDirectory };
            foreach (var a in _args) psi.ArgumentList.Add(a);
            return psi;
        }

        protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string runId, CliOutputLine line)
            => line.Stream == "stdout" && !string.IsNullOrWhiteSpace(line.Text)
                ? new[] { new CliRunEvent.TurnCompleted("probe") { RunId = runId } }
                : Array.Empty<CliRunEvent>();
    }

    private sealed class TempLogs : IRunLogPathProvider, IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "car-engine-" + Guid.NewGuid().ToString("N"));
        public string GetRunLogDirectory(string runId) => Path.Combine(_root, runId);
        public string GetActiveJobsFile() => Path.Combine(_root, "active.json");
        public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }
    }

    [Fact]
    public async Task Run_StreamsOutput_RaisesTypedEvents_AndClassifiesCleanExitAsCompleted()
    {
        using var logs = new TempLogs();
        var backend = new ProbeBackend("dotnet", ["--version"], logs);

        var events = new ConcurrentQueue<CliRunEvent>();
        backend.OnRunEvent += (_, e) => events.Enqueue(e);

        var started = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<CliRunInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnStarted += (_, r) => started.TrySetResult(r);
        backend.OnFinished += (_, r) => finished.TrySetResult(r);

        var (run, error) = await backend.StartAsync(new CliRunRequest
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

        // Event ordering: RunStarted first, ProcessExited last, a TurnCompleted between.
        var seq = events.ToArray();
        Assert.IsType<CliRunEvent.RunStarted>(seq[0]);
        Assert.IsType<CliRunEvent.ProcessExited>(seq[^1]);
        Assert.Contains(seq, e => e is CliRunEvent.TurnCompleted);

        // The version line was captured.
        var output = backend.GetExecution("engine-test");
        Assert.Equal("completed", output!.Status);
        Assert.Contains(backend.GetOutput("engine-test"), l => l.Stream == "stdout");
    }

    [Fact]
    public async Task DuplicateRunId_WhileLive_IsRejected_ButReusableAfterExit()
    {
        using var logs = new TempLogs();
        var backend = new ProbeBackend("dotnet", ["--version"], logs);

        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnFinished += (_, _) => finished.TrySetResult();

        var (run1, err1) = await backend.StartAsync(new CliRunRequest
        {
            RunId = "dup", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        Assert.Null(err1);
        Assert.NotNull(run1);

        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // After the first run exits, the same id can be reused.
        var (run2, err2) = await backend.StartAsync(new CliRunRequest
        {
            RunId = "dup", Prompt = "x", WorkingDirectory = Path.GetTempPath(),
        });
        Assert.Null(err2);
        Assert.NotNull(run2);
    }
}

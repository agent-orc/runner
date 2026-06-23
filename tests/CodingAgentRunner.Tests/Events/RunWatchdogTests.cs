using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Events;

/// <summary>
/// Deterministic RunWatchdog wiring tests via a fake driver — phase tracking, run
/// isolation, robustness to out-of-order events, and lifecycle safety. (Time-based
/// escalation is covered by the real-process auto-stop test in CliDriverEngineTests.)
/// </summary>
public class RunWatchdogTests
{
#pragma warning disable CS0067 // events are part of the ICliDriver contract; not all are raised here
    private sealed class FakeDriver : ICliDriver
    {
        public string CliType => "fake";
        public string GetCliPath() => "fake";
        public bool IsAvailable() => true;
        public (bool Available, string? Version, string Path) TestCliPath(string? path = null) => (true, "1.0", "fake");
        public Task<(CliRunInfo? Run, string? Error)> StartAsync(CliRunRequest request, CancellationToken ct = default)
            => Task.FromResult<(CliRunInfo?, string?)>((null, null));
        public IAsyncEnumerable<CliRunEvent> StreamAsync(CliRunRequest request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public bool Stop(string runId, RunStopReason reason = RunStopReason.UserStop) { Stopped.Add(runId); return true; }
        public bool SendInput(string runId, string input) => false;
        public IReadOnlyList<CliOutputLine> GetOutput(string runId) => Array.Empty<CliOutputLine>();
        public CliRunInfo? GetExecution(string runId) => null;
        public bool IsCompatibleSessionId(string? sessionId) => true;
        public bool Forget(string runId) => false;
        public bool SupportsCleanContext => false;
        public CliCapabilities Capabilities(string? model) => new();

        public event Action<string, CliOutputLine>? OnOutput;
        public event Action<string, CliRunInfo>? OnStarted;
        public event Action<string, CliRunInfo>? OnFinished;
        public event Action<string, CliRunEvent>? OnRunEvent;

        public List<string> Stopped { get; } = new();
        public void RaiseStarted(string runId) => OnStarted?.Invoke(runId, new CliRunInfo { RunId = runId, StartedAt = DateTime.UtcNow });
        public void Raise(string runId, CliRunEvent e) => OnRunEvent?.Invoke(runId, e);
        public void RaiseFinished(string runId) => OnFinished?.Invoke(runId, new CliRunInfo { RunId = runId });
    }
#pragma warning restore CS0067

    [Fact]
    public void PhaseOf_UntrackedRun_IsNull()
    {
        var d = new FakeDriver();
        using var wd = RunWatchdog.Attach(d);
        Assert.Null(wd.PhaseOf("never-started"));
    }

    [Fact]
    public void Phase_AdvancesFromEvents_PerRun_Isolated()
    {
        var d = new FakeDriver();
        using var wd = RunWatchdog.Attach(d);

        d.RaiseStarted("a");
        d.RaiseStarted("b");
        Assert.Equal(RunPhase.Spawning, wd.PhaseOf("a"));
        Assert.Equal(RunPhase.Spawning, wd.PhaseOf("b"));

        d.Raise("a", new CliRunEvent.SessionStarted("s") { RunId = "a" });   // a → PromptConsumed
        d.Raise("a", new CliRunEvent.TurnStarted() { RunId = "a" });         // a → TurnInProgress
        Assert.Equal(RunPhase.TurnInProgress, wd.PhaseOf("a"));
        Assert.Equal(RunPhase.Spawning, wd.PhaseOf("b"));                    // b untouched — runs are isolated
    }

    [Fact]
    public void EventForUntrackedRun_IsIgnored()   // OnStarted missed → no phantom tracking
    {
        var d = new FakeDriver();
        using var wd = RunWatchdog.Attach(d);
        d.Raise("ghost", new CliRunEvent.OutputDelta("x") { RunId = "ghost" });
        Assert.Null(wd.PhaseOf("ghost"));
    }

    [Fact]
    public void Finished_DropsTracking()
    {
        var d = new FakeDriver();
        using var wd = RunWatchdog.Attach(d);
        d.RaiseStarted("r");
        Assert.NotNull(wd.PhaseOf("r"));
        d.RaiseFinished("r");
        Assert.Null(wd.PhaseOf("r"));
    }

    [Fact]
    public void AttachThenImmediateDispose_AndDoubleDispose_DoNotThrow()
    {
        var d = new FakeDriver();
        var wd = RunWatchdog.Attach(d);
        wd.Dispose();
        wd.Dispose();   // idempotent
        // After dispose, the watchdog no longer tracks events.
        d.RaiseStarted("r");
        Assert.Null(wd.PhaseOf("r"));
    }
}

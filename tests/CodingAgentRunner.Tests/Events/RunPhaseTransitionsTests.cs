using CodingAgentRunner.Events;
using Xunit;

namespace CodingAgentRunner.Tests.Events;

public class RunPhaseTransitionsTests
{
    [Fact]
    public void HappyPath_WalksSpawningToTurnCompleted()
    {
        var phase = RunPhase.Spawning;
        phase = RunPhaseTransitions.Apply(phase, new CliRunEvent.RunStarted(123, "claude", "claude-opus-4-8"));
        Assert.Equal(RunPhase.Spawning, phase);
        phase = RunPhaseTransitions.Apply(phase, new CliRunEvent.SessionInitializing());
        Assert.Equal(RunPhase.SessionInitializing, phase);
        phase = RunPhaseTransitions.Apply(phase, new CliRunEvent.SessionStarted("sess-1"));
        Assert.Equal(RunPhase.PromptConsumed, phase);
        phase = RunPhaseTransitions.Apply(phase, new CliRunEvent.TurnStarted());
        Assert.Equal(RunPhase.TurnInProgress, phase);
        phase = RunPhaseTransitions.Apply(phase, new CliRunEvent.OutputDelta("hi"));
        Assert.Equal(RunPhase.OutputDelta, phase);
        phase = RunPhaseTransitions.Apply(phase, new CliRunEvent.TurnCompleted("12k tokens"));
        Assert.Equal(RunPhase.TurnCompleted, phase);
    }

    [Fact]
    public void ToolCompleted_ReturnsToTurnInProgress_OnlyFromToolExecuting()
    {
        // From ToolExecuting -> back to TurnInProgress (the model resumed talking).
        var afterTool = RunPhaseTransitions.Apply(RunPhase.ToolExecuting,
            new CliRunEvent.ToolCompleted("Read", false, "ok"));
        Assert.Equal(RunPhase.TurnInProgress, afterTool);

        // From any other phase, ToolCompleted is a no-op (out-of-order frame).
        var fromOutput = RunPhaseTransitions.Apply(RunPhase.OutputDelta,
            new CliRunEvent.ToolCompleted("Read", false, "ok"));
        Assert.Equal(RunPhase.OutputDelta, fromOutput);
    }

    [Fact]
    public void Heartbeat_And_RateLimit_DoNotChangePhase()
    {
        Assert.Equal(RunPhase.TurnInProgress,
            RunPhaseTransitions.Apply(RunPhase.TurnInProgress, new CliRunEvent.Heartbeat()));
        Assert.Equal(RunPhase.OutputDelta,
            RunPhaseTransitions.Apply(RunPhase.OutputDelta,
                new CliRunEvent.RateLimitObserved("5h", "active", 0, null, false)));
    }

    [Fact]
    public void Unknown_OnlyDerailsFromSpawning()
    {
        Assert.Equal(RunPhase.Unknown,
            RunPhaseTransitions.Apply(RunPhase.Spawning, new CliRunEvent.Unknown("???")));
        // Mid-turn, an unclassified frame must not knock the phase back.
        Assert.Equal(RunPhase.TurnInProgress,
            RunPhaseTransitions.Apply(RunPhase.TurnInProgress, new CliRunEvent.Unknown("???")));
    }

    [Fact]
    public void ApprovalRequested_MapsToNeedsInput()
    {
        Assert.Equal(RunPhase.NeedsInput,
            RunPhaseTransitions.Apply(RunPhase.TurnInProgress,
                new CliRunEvent.ApprovalRequested("approve edit?")));
    }

    [Theory]
    [InlineData(typeof(CliRunEvent.OutputDelta), true)]
    [InlineData(typeof(CliRunEvent.ToolStarted), true)]
    [InlineData(typeof(CliRunEvent.ToolCompleted), true)]
    [InlineData(typeof(CliRunEvent.Heartbeat), true)]
    [InlineData(typeof(CliRunEvent.Unknown), true)]
    [InlineData(typeof(CliRunEvent.SessionInitializing), false)]
    public void IsActivitySignal_CountsOutputAndToolAndHeartbeat(System.Type evtType, bool expected)
    {
        CliRunEvent evt = evtType.Name switch
        {
            nameof(CliRunEvent.OutputDelta) => new CliRunEvent.OutputDelta("x"),
            nameof(CliRunEvent.ToolStarted) => new CliRunEvent.ToolStarted("Bash", null),
            nameof(CliRunEvent.ToolCompleted) => new CliRunEvent.ToolCompleted("Bash", false, null),
            nameof(CliRunEvent.Heartbeat) => new CliRunEvent.Heartbeat(),
            nameof(CliRunEvent.Unknown) => new CliRunEvent.Unknown("x"),
            _ => new CliRunEvent.SessionInitializing(),
        };
        Assert.Equal(expected, RunPhaseTransitions.IsActivitySignal(evt));
    }
}

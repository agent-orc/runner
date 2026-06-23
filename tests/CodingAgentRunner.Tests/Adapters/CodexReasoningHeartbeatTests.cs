using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using Xunit;

namespace CodingAgentRunner.Tests.Adapters;

/// <summary>
/// Load-bearing watchdog contract for Codex xhigh: a reasoning block is a liveness ping
/// (Heartbeat), not a tool, and it must keep a long silent reasoning run alive. Without
/// this guarantee a 7+ minute pre-turn reasoning block would be misclassified as hung
/// and killed. (Consolidated from the consuming app — the library owns this contract.)
/// </summary>
public sealed class CodexReasoningHeartbeatTests
{
    private const string Jk = "job-1";

    [Fact]
    public void ItemCompletedReasoning_EmitsHeartbeat_NotToolCompleted()
    {
        const string frame = """{"type":"item.completed","item":{"type":"reasoning","text":"Let me think about this..."}}""";
        var evt = Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList());
        Assert.IsType<CliRunEvent.Heartbeat>(evt);
        Assert.IsNotType<CliRunEvent.ToolCompleted>(evt);
        Assert.Equal(Jk, evt.RunId);
        Assert.True(RunPhaseTransitions.IsActivitySignal(evt), "A reasoning Heartbeat must reset the silence clock.");
    }

    [Fact]
    public void ItemStartedReasoning_EmitsHeartbeat_NotToolStarted()
    {
        const string frame = """{"type":"item.started","item":{"type":"reasoning"}}""";
        var evt = Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList());
        Assert.IsType<CliRunEvent.Heartbeat>(evt);
        Assert.IsNotType<CliRunEvent.ToolStarted>(evt);
    }

    [Fact]
    public void ReasoningHeartbeat_KeepsPhaseUnchanged_NoFalseToolExecuting()
    {
        // The ping must NOT advance the phase: during pre-turn xhigh reasoning the run is
        // still PromptConsumed; mis-advancing to ToolExecuting would mask a genuine hang
        // under the wider tool budget.
        const string frame = """{"type":"item.completed","item":{"type":"reasoning","text":"thinking"}}""";
        var evt = Assert.Single(CodexEventAdapter.Map(frame, Jk).ToList());
        Assert.Equal(RunPhase.PromptConsumed, RunPhaseTransitions.Apply(RunPhase.PromptConsumed, evt));
    }

    [Fact]
    public void ReasoningPings_KeepXhighRunHealthy_WhileAFrameLessRunIsKilled()
    {
        // A run that reasons silently for >7 min is NOT killed because each reasoning frame
        // resets the silence clock — not merely because the budget is wide.
        var policy = WatchdogPolicy.Default with
        {
            WarmUpGraceSeconds = 0,
            QuietSeconds = 30,
            Budgets = new Dictionary<RunPhase, PhaseBudget> { [RunPhase.PromptConsumed] = new(60, 120) },
        };

        const string reasoning = """{"type":"item.completed","item":{"type":"reasoning","text":"step"}}""";
        var lastActivity = 0.0;
        var phase = RunPhase.PromptConsumed;
        for (var t = 90.0; t <= 480.0; t += 90.0)   // a reasoning frame every 90 s for 8 min
        {
            var silenceBeforePing = t - lastActivity;   // only grows to the inter-frame gap
            Assert.NotEqual(WatchdogState.Hung, policy.Decide(phase, silenceBeforePing, t));

            var evt = Assert.Single(CodexEventAdapter.Map(reasoning, Jk).ToList());
            phase = RunPhaseTransitions.Apply(phase, evt);
            if (RunPhaseTransitions.IsActivitySignal(evt)) lastActivity = t;
        }
        Assert.Equal(RunPhase.PromptConsumed, phase);   // never went Hung, still pre-turn

        // Counter-case: the SAME window with NO frames crosses the kill budget and dies.
        Assert.Equal(WatchdogState.Hung, policy.Decide(RunPhase.PromptConsumed, silenceSeconds: 200, runAgeSeconds: 400));
    }
}

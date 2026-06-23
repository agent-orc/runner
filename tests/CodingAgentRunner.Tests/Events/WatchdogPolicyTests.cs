using CodingAgentRunner.Events;
using Xunit;

namespace CodingAgentRunner.Tests.Events;

public class WatchdogPolicyTests
{
    [Fact]
    public void WarmUpGrace_KeepsHealthy_EvenWhenSilent()
    {
        var p = WatchdogPolicy.Default;   // 10s grace
        Assert.Equal(WatchdogState.Healthy, p.Decide(RunPhase.Spawning, silenceSeconds: 9999, runAgeSeconds: 5));
    }

    [Fact]
    public void Spawning_EscalatesBySilence()
    {
        var p = WatchdogPolicy.Default;   // Spawning budget (30, 60); Quiet 30 == Suspicious → no Quiet band here
        Assert.Equal(WatchdogState.Healthy,    p.Decide(RunPhase.Spawning, 10, 100));
        Assert.Equal(WatchdogState.Suspicious, p.Decide(RunPhase.Spawning, 30, 100));  // 30 hits the suspicious budget
        Assert.Equal(WatchdogState.Suspicious, p.Decide(RunPhase.Spawning, 45, 100));
        Assert.Equal(WatchdogState.Hung,       p.Decide(RunPhase.Spawning, 90, 100));
    }

    [Fact]
    public void ToolExecuting_ToleratesLongSilence()
    {
        var p = WatchdogPolicy.Default;   // ToolExecuting budget (300, 1200)
        Assert.Equal(WatchdogState.Quiet,      p.Decide(RunPhase.ToolExecuting, 200, 1000));
        Assert.Equal(WatchdogState.Suspicious, p.Decide(RunPhase.ToolExecuting, 400, 1000));
        Assert.NotEqual(WatchdogState.Hung,    p.Decide(RunPhase.ToolExecuting, 1000, 2000));
    }

    [Fact]
    public void NeedsInput_NeverEscalates()   // waiting on a human is not a stall
    {
        var p = WatchdogPolicy.Default;
        var state = p.Decide(RunPhase.NeedsInput, 5000, 6000);   // huge silence on purpose
        Assert.True(state is WatchdogState.Healthy or WatchdogState.Quiet,
            $"NeedsInput must never reach Suspicious/Hung, was {state}");
    }

    [Fact]
    public void Disabled_AlwaysHealthy()
    {
        var p = WatchdogPolicy.Default with { Enabled = false };
        Assert.Equal(WatchdogState.Healthy, p.Decide(RunPhase.Spawning, 99999, 99999));
    }

    [Fact]
    public void PhaseBudget_RejectsNegativeValues()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhaseBudget(-1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PhaseBudget(10, -1));
    }

    [Fact]
    public void Decide_ClampsNegativeSilence()   // clock skew must not be read as a negative silence
        => Assert.Equal(WatchdogState.Healthy, WatchdogPolicy.Default.Decide(RunPhase.Spawning, silenceSeconds: -100, runAgeSeconds: 100));

    [Fact]
    public void Decide_ThresholdsAreInclusive()
    {
        var p = WatchdogPolicy.Default with
        {
            QuietSeconds = 10,
            Budgets = new Dictionary<RunPhase, PhaseBudget> { [RunPhase.TurnInProgress] = new(20, 40) },
        };
        Assert.Equal(WatchdogState.Healthy,    p.Decide(RunPhase.TurnInProgress, 9, 100));
        Assert.Equal(WatchdogState.Quiet,      p.Decide(RunPhase.TurnInProgress, 10, 100));   // == QuietSeconds
        Assert.Equal(WatchdogState.Suspicious, p.Decide(RunPhase.TurnInProgress, 20, 100));   // == SuspiciousSeconds
        Assert.Equal(WatchdogState.Hung,       p.Decide(RunPhase.TurnInProgress, 40, 100));   // == HungSeconds
    }

    [Fact]
    public void Decide_WarmUpGrace_IsExclusiveBoundary()
    {
        var p = WatchdogPolicy.Default with { WarmUpGraceSeconds = 10 };
        Assert.Equal(WatchdogState.Healthy, p.Decide(RunPhase.Spawning, 9999, runAgeSeconds: 9.999));  // under grace
        Assert.Equal(WatchdogState.Hung,    p.Decide(RunPhase.Spawning, 9999, runAgeSeconds: 10));     // grace lapsed
    }

    [Fact]
    public void DefaultBudgets_CoverEveryRunPhase()   // a new RunPhase must get its own explicit budget
    {
        foreach (RunPhase phase in System.Enum.GetValues<RunPhase>())
            Assert.True(WatchdogPolicy.Default.Budgets.ContainsKey(phase), $"missing budget for {phase}");
    }

    [Fact]
    public void UnlistedPhase_FallsBackToAConservativeBudget()
    {
        var p = WatchdogPolicy.Default with { Budgets = new Dictionary<RunPhase, PhaseBudget>() };  // fallback (60, 180)
        Assert.Equal(WatchdogState.Suspicious, p.Decide(RunPhase.TurnInProgress, 60, 100));
        Assert.Equal(WatchdogState.Hung,       p.Decide(RunPhase.TurnInProgress, 180, 100));
    }
}

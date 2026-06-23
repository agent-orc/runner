namespace CodingAgentRunner.Events;

/// <summary>How healthy a run looks to the watchdog, by how long it has been silent.</summary>
public enum WatchdogState
{
    /// <summary>Producing activity within budget.</summary>
    Healthy,
    /// <summary>Quiet longer than the routine threshold, but well within budget.</summary>
    Quiet,
    /// <summary>Silent past the phase's suspicious budget — worth surfacing.</summary>
    Suspicious,
    /// <summary>Silent past the phase's hung budget — should be stopped.</summary>
    Hung,
}

/// <summary>Silence budgets for one <see cref="RunPhase"/>: warn at <paramref name="SuspiciousSeconds"/>, kill at <paramref name="HungSeconds"/>. Both must be &#8805; 0.</summary>
public sealed record PhaseBudget(double SuspiciousSeconds, double HungSeconds)
{
    /// <summary>Silence (seconds) at which the run reports <see cref="WatchdogState.Suspicious"/>.</summary>
    public double SuspiciousSeconds { get; init; } = SuspiciousSeconds >= 0
        ? SuspiciousSeconds
        : throw new ArgumentOutOfRangeException(nameof(SuspiciousSeconds), "must be >= 0");

    /// <summary>Silence (seconds) at which the run reports <see cref="WatchdogState.Hung"/>.</summary>
    public double HungSeconds { get; init; } = HungSeconds >= 0
        ? HungSeconds
        : throw new ArgumentOutOfRangeException(nameof(HungSeconds), "must be >= 0");
}

/// <summary>
/// A declarative, phase-aware watchdog policy. Different lifecycle phases tolerate
/// different silences — a model still "thinking" inside a tool call is fine for
/// minutes, whereas no handshake right after spawn is a fast failure — so the budget
/// is keyed by <see cref="RunPhase"/>. <see cref="Decide"/> is a pure function of
/// (phase, silence, age); attach a live driver with <see cref="RunWatchdog"/> to have
/// it enforced automatically.
/// </summary>
public sealed record WatchdogPolicy
{
    /// <summary>Turns the whole watchdog off when false (always <see cref="WatchdogState.Healthy"/>).</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>No verdict harsher than Healthy until the run is at least this old (covers slow spawns).</summary>
    public double WarmUpGraceSeconds { get; init; } = 10;

    /// <summary>Silence past this (but under the phase's suspicious budget) reports <see cref="WatchdogState.Quiet"/>.</summary>
    public double QuietSeconds { get; init; } = 30;

    /// <summary>How often <see cref="RunWatchdog"/> re-evaluates live runs.</summary>
    public double TickSeconds { get; init; } = 5;

    /// <summary>Per-phase silence budgets. Missing phases fall back to a conservative default.</summary>
    public IReadOnlyDictionary<RunPhase, PhaseBudget> Budgets { get; init; } = DefaultBudgets;

    // NOTE: these statics MUST be declared before Default — Default = new() reads
    // DefaultBudgets during static init, so it has to be initialized first.
    private static readonly PhaseBudget FallbackBudget = new(60, 180);

    private static readonly IReadOnlyDictionary<RunPhase, PhaseBudget> DefaultBudgets =
        new Dictionary<RunPhase, PhaseBudget>
        {
            [RunPhase.Spawning]            = new(30,   60),
            [RunPhase.SessionInitializing] = new(120,  600),
            [RunPhase.PromptConsumed]      = new(300,  1200),
            [RunPhase.TurnInProgress]      = new(180,  600),
            [RunPhase.OutputDelta]         = new(180,  600),
            [RunPhase.ToolExecuting]       = new(300,  1200),
            [RunPhase.TurnCompleted]       = new(120,  600),
            [RunPhase.TurnFailed]          = new(120,  600),
            [RunPhase.NeedsInput]          = new(9999, 9999),   // waiting on a human is not a stall
            [RunPhase.Exited]              = new(9999, 9999),
            [RunPhase.Killed]              = new(9999, 9999),
            [RunPhase.Unknown]             = new(60,   240),
        };

    /// <summary>The default policy: sensible per-phase budgets, 10s warm-up, 5s tick.</summary>
    public static WatchdogPolicy Default { get; } = new();

    /// <summary>The budget for a phase, or a conservative fallback when unlisted.</summary>
    public PhaseBudget BudgetFor(RunPhase phase)
        => Budgets.TryGetValue(phase, out var b) ? b : FallbackBudget;

    /// <summary>
    /// Pure verdict for a run that has been silent for <paramref name="silenceSeconds"/>,
    /// is <paramref name="runAgeSeconds"/> old, and sits in <paramref name="phase"/>.
    /// </summary>
    public WatchdogState Decide(RunPhase phase, double silenceSeconds, double runAgeSeconds)
    {
        if (!Enabled) return WatchdogState.Healthy;
        if (runAgeSeconds < WarmUpGraceSeconds) return WatchdogState.Healthy;
        if (silenceSeconds < 0) silenceSeconds = 0;   // clock skew → never report a negative silence

        var b = BudgetFor(phase);
        if (silenceSeconds >= b.HungSeconds) return WatchdogState.Hung;
        if (silenceSeconds >= b.SuspiciousSeconds) return WatchdogState.Suspicious;
        if (silenceSeconds >= QuietSeconds) return WatchdogState.Quiet;
        return WatchdogState.Healthy;
    }
}

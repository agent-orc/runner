namespace CodingAgentRunner.Model;

/// <summary>
/// Why a run was stopped by the runner. <see cref="None"/> means it was NOT
/// runner-initiated — the process ended on its own (a clean exit or a self-crash) —
/// which is exactly what <see cref="RunStatusClassifier"/> keys on to tell
/// "I stopped it" apart from "it died".
/// </summary>
public enum RunStopReason
{
    /// <summary>Not stopped by the runner; the process ended on its own.</summary>
    None = 0,

    /// <summary>A consumer asked to stop the run.</summary>
    UserStop,

    /// <summary>Paused to send a follow-up; not a crash.</summary>
    FollowupPause,

    /// <summary>The silence watchdog stopped a hung run.</summary>
    Watchdog,

    /// <summary>A cancellation token fired.</summary>
    Cancelled,

    /// <summary>A configured quota cap was exceeded.</summary>
    QuotaCapExceeded,

    /// <summary>The runner stopped a quota-limited process before waiting for its reset.</summary>
    QuotaResetWait,

    /// <summary>A consumer-defined completion sentinel was detected (optional).</summary>
    SentinelDetected,

    /// <summary>The environment blocked the run (e.g. a sandbox refusal).</summary>
    EnvironmentBlocker,

    /// <summary>The CLI signalled completion without a clean exit path (legacy CLIs).</summary>
    SilentCompletion,
}

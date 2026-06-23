namespace CodingAgentRunner.Model;

/// <summary>The terminal status of a run.</summary>
public enum RunStatus
{
    /// <summary>The CLI finished on its own, cleanly (exit 0, no runner stop).</summary>
    Completed,

    /// <summary>The runner stopped it on purpose (a <see cref="RunStopReason"/> was set).</summary>
    Stopped,

    /// <summary>The CLI ended on its own with a non-zero / unknown exit — a self-crash.</summary>
    Failed,
}

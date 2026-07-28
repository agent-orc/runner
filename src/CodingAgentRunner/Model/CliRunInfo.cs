namespace CodingAgentRunner.Model;

/// <summary>
/// A snapshot of one CLI run the runner is tracking. <see cref="Status"/> is the
/// coarse live string (<c>running</c> while in flight); once the process ends the
/// terminal outcome is derived with <see cref="RunStatusClassifier"/> from the
/// exit code plus any <see cref="RunStopReason"/>.
/// </summary>
public record CliRunInfo
{
    /// <summary>Consumer-assigned correlation id for the run.</summary>
    public string RunId { get; init; } = "";
    /// <summary>OS process id of the spawned CLI.</summary>
    public int ProcessId { get; init; }
    /// <summary>UTC instant the process was spawned.</summary>
    public DateTime StartedAt { get; init; }
    /// <summary><c>running</c> | <c>completed</c> | <c>failed</c> | <c>cancelled</c>.</summary>
    public string Status { get; init; } = "";
    /// <summary>OS exit code once the process has ended; null while running.</summary>
    public int? ExitCode { get; init; }
    /// <summary>Wall-clock duration in seconds once the process has ended; null while running.</summary>
    public double? DurationSeconds { get; init; }
    /// <summary>Model the run was invoked with, when known.</summary>
    public string? Model { get; init; }
    /// <summary>Thinking / reasoning level the run was invoked with, when applicable.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>
    /// Absolute path of the isolated clean-context home this run was given
    /// (<c>ContextMode=clean</c> on a CLI that supports it), else null. Surfaced
    /// so a consumer can point its own per-run side-channels (e.g. a session-file
    /// liveness watcher) at the home the CLI is actually writing to instead of the
    /// user's real home — without it such a watcher sees permanent silence on a
    /// clean run and a watchdog would false-kill a live run.
    /// </summary>
    public string? CleanContextHome { get; init; }

    /// <summary>
    /// Names of the subagents this run can delegate to — the definitions the runner
    /// materialized plus any the repository provides. Empty when the CLI has no
    /// subagent convention or delegation is off. Surfaced so a token ledger can tell
    /// which cheap workers a run had available when it attributes its costs.
    /// </summary>
    public IReadOnlyList<string> Subagents { get; init; } = [];
}

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
}

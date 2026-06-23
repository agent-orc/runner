using CodingAgentRunner.Events;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Execution;

/// <summary>
/// The surface every CLI backend exposes. A consumer resolves a backend for a CLI
/// type, subscribes to <see cref="OnRunEvent"/> (and optionally the raw streams),
/// and drives runs with <see cref="StartAsync"/> / <see cref="Stop"/> — without
/// knowing which CLI executes a given run.
/// </summary>
public interface ICliBackend
{
    /// <summary>One of <see cref="CliTypes"/>.</summary>
    string CliType { get; }

    /// <summary>The configured command / path used to invoke this CLI.</summary>
    string GetCliPath();

    /// <summary>Whether the CLI responds to a <c>--version</c> probe.</summary>
    bool IsAvailable();

    /// <summary>Probe a CLI path (or the configured one) for availability + version.</summary>
    (bool Available, string? Version, string Path) TestCliPath(string? path = null);

    /// <summary>Start a run. Returns the live <see cref="CliRunInfo"/>, or an error string.</summary>
    Task<(CliRunInfo? Run, string? Error)> StartAsync(CliRunRequest request, CancellationToken ct = default);

    /// <summary>
    /// Terminate the live process for <paramref name="runId"/>. The
    /// <paramref name="reason"/> flows into the outcome classifier so a deliberate
    /// stop is reported as stopped, not as a failed self-crash. Returns false when
    /// no process is tracked under that id.
    /// </summary>
    bool Stop(string runId, RunStopReason reason = RunStopReason.UserStop);

    /// <summary>Write a line to the live run's stdin. Returns false when the run is unknown / exited.</summary>
    bool SendInput(string runId, string input);

    /// <summary>The captured output lines for a run (live buffer, or the persisted log after eviction).</summary>
    IReadOnlyList<CliOutputLine> GetOutput(string runId);

    /// <summary>The tracked run info for <paramref name="runId"/>, or null when unknown.</summary>
    CliRunInfo? GetExecution(string runId);

    /// <summary>Whether this backend can isolate its persistent state for a clean run.</summary>
    bool SupportsCleanContext { get; }

    /// <summary>Raw output lines as they stream (one per stdout/stderr/system line).</summary>
    event Action<string, CliOutputLine>? OnOutput;

    /// <summary>Raised once when a run's process has started.</summary>
    event Action<string, CliRunInfo>? OnStarted;

    /// <summary>Raised once when a run's process has exited and been classified.</summary>
    event Action<string, CliRunInfo>? OnFinished;

    /// <summary>Typed lifecycle events — the primary contract for driving a phase-aware watchdog.</summary>
    event Action<string, CliRunEvent>? OnRunEvent;
}

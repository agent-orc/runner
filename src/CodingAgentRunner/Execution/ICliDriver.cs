using CodingAgentRunner.Events;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Execution;

/// <summary>
/// The surface every CLI driver exposes. A consumer resolves a driver for a CLI
/// type, subscribes to <see cref="OnRunEvent"/> (and optionally the raw streams),
/// and drives runs with <see cref="StartAsync"/> / <see cref="Stop"/> — without
/// knowing which CLI executes a given run.
/// </summary>
public interface ICliDriver
{
    /// <summary>One of <see cref="CliTypes"/>.</summary>
    string CliType { get; }

    /// <summary>The configured command / path used to invoke this CLI.</summary>
    string GetCliPath();

    /// <summary>Whether the CLI responds to a <c>--version</c> probe.</summary>
    bool IsAvailable();

    /// <summary>Probe a CLI path (or the configured one) for availability + version.</summary>
    (bool Available, string? Version, string Path) TestCliPath(string? path = null);

    /// <summary>
    /// Checks that this CLI is ready to start and applies its built-in recoverable
    /// repairs when needed. This is the same policy run immediately before spawn,
    /// but does not start an agent run. Cancellation is propagated to the health
    /// operation.
    /// </summary>
    Task<PreSpawnHealthResult> EnsureHealthyAsync(CancellationToken ct = default);

    /// <summary>Start a run. Returns the live <see cref="CliRunInfo"/>, or an error string.</summary>
    Task<(CliRunInfo? Run, string? Error)> StartAsync(CliRunRequest request, CancellationToken ct = default);

    /// <summary>
    /// Start a run and consume its typed events as a pull-stream — the single-run
    /// ergonomic alternative to wiring <see cref="OnRunEvent"/> + <see cref="OnFinished"/>.
    /// The sequence ends after the run's terminal event (<see cref="CliRunEvent.RunEnded"/>);
    /// a spawn failure surfaces as a thrown exception,
    /// and cancelling <paramref name="ct"/> stops the run and ends the enumeration.
    /// For multiplexing many concurrent runs through one handler, use <see cref="OnRunEvent"/>
    /// and route by <c>evt.RunId</c> instead.
    /// <para>Single-consumer: only one <c>StreamAsync</c> may be active per run id at a time
    /// (a second concurrent one throws). The buffer is unbounded, so consume promptly — a
    /// stalled <c>await foreach</c> on a high-volume run lets events accumulate in memory.</para>
    /// </summary>
    IAsyncEnumerable<CliRunEvent> StreamAsync(CliRunRequest request, CancellationToken ct = default);

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

    /// <summary>
    /// Whether <paramref name="sessionId"/> is a session this CLI can resume.
    /// Lets a consumer pre-validate a recorded session before requesting a resume —
    /// e.g. Codex only resumes a UUID, so feeding it a slug from another CLI must be
    /// rejected rather than silently starting fresh. Default drivers accept any.
    /// </summary>
    bool IsCompatibleSessionId(string? sessionId);

    /// <summary>
    /// Drop the in-memory tracking for a finished run, releasing its output buffer
    /// and file handles. After this, <see cref="GetOutput"/> reads from the persisted
    /// log and <see cref="GetExecution"/> returns null. The engine does NOT auto-evict
    /// finished runs (so the result stays readable); call this once you have consumed
    /// a run's outcome. Returns false when the id is unknown.
    /// </summary>
    bool Forget(string runId);

    /// <summary>Whether this driver can isolate its persistent state for a clean run.</summary>
    bool SupportsCleanContext { get; }

    /// <summary>
    /// Create an isolated CLI home that the caller owns until it explicitly disposes
    /// the returned lease. Supply the lease through
    /// <see cref="CliRunRequest.CleanContextLease"/> to reuse it across fresh or
    /// resumed attempts. The runner does not dispose a supplied lease when an
    /// attempt ends.
    /// </summary>
    /// <exception cref="InvalidOperationException">The CLI does not support clean contexts, or the home could not be prepared.</exception>
    CliCleanContextLease AcquireCleanContext()
        => throw new InvalidOperationException($"{CliType} does not support clean contexts.");

    /// <summary>
    /// What this CLI + model can do — supported reasoning levels, clean-context and
    /// resume support, and any CLI-specific knobs. Lets a UI render exactly the
    /// controls that apply to the selected CLI/model instead of a generalized set.
    /// Pass the model id you intend to run; reasoning levels are per-model, so null
    /// (no model) yields an empty reasoning ladder rather than a guess.
    /// <para>Pure and total: safe to call with any (or null) model id — an unknown model
    /// just yields empty capability lists, never an exception.</para>
    /// </summary>
    CliCapabilities Capabilities(string? model);

    /// <summary>Raw output lines as they stream (one per stdout/stderr/system line).</summary>
    event Action<string, CliOutputLine>? OnOutput;

    /// <summary>Raised once when a run's process has started.</summary>
    event Action<string, CliRunInfo>? OnStarted;

    /// <summary>Raised once when a run's process has exited and been classified.</summary>
    event Action<string, CliRunInfo>? OnFinished;

    /// <summary>Typed lifecycle events — the primary contract for driving a phase-aware watchdog.</summary>
    event Action<string, CliRunEvent>? OnRunEvent;
}

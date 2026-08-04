using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Model;
using Microsoft.Extensions.Logging;

namespace CodingAgentRunner.Execution;

/// <summary>Produces the <see cref="CliCapabilities"/> for a model — the descriptor seam that replaces a driver's <c>Capabilities</c> override.</summary>
public delegate CliCapabilities CliCapabilitiesProvider(string? model);

/// <summary>
/// What a <see cref="PreSpawnHealth"/> check gets: a probe for the CLI's availability
/// (the engine's <c>TestCliPath</c>) and a logger. Lets the heal decide and report
/// without holding engine state.
/// </summary>
/// <param name="Probe">Re-probe the CLI's availability (path + <c>--version</c> verdict).</param>
/// <param name="Logger">Diagnostics logger for the heal.</param>
public readonly record struct PreSpawnHealthContext(
    Func<(bool Available, string? Version, string Path)> Probe,
    ILogger Logger);

/// <summary>The outcome of a CLI health check performed before spawn.</summary>
public enum PreSpawnHealthStatus
{
    /// <summary>The CLI was already ready to start.</summary>
    Healthy,

    /// <summary>A known recoverable problem was repaired and the CLI is ready to start.</summary>
    Repaired,

    /// <summary>The CLI is not ready to start. <see cref="PreSpawnHealthResult.Error"/> explains why.</summary>
    Failed,
}

/// <summary>
/// A typed, actionable outcome from <see cref="ICliDriver.EnsureHealthyAsync"/>.
/// This operation only probes and, where the descriptor supports it, repairs the
/// local CLI installation; it never starts an agent run.
/// </summary>
public sealed record PreSpawnHealthResult
{
    /// <summary>The health verdict.</summary>
    public required PreSpawnHealthStatus Status { get; init; }

    /// <summary>True when the CLI is ready to start.</summary>
    public bool IsHealthy => Status is PreSpawnHealthStatus.Healthy or PreSpawnHealthStatus.Repaired;

    /// <summary>Actions taken while repairing the installation.</summary>
    public IReadOnlyList<string> Actions { get; init; } = Array.Empty<string>();

    /// <summary>An actionable failure explanation when <see cref="Status"/> is <see cref="PreSpawnHealthStatus.Failed"/>.</summary>
    public string? Error { get; init; }

    /// <summary>Creates an already-healthy result.</summary>
    public static PreSpawnHealthResult Healthy() => new() { Status = PreSpawnHealthStatus.Healthy };

    /// <summary>Creates a successful repair result.</summary>
    public static PreSpawnHealthResult Repaired(IReadOnlyList<string>? actions = null) => new()
    {
        Status = PreSpawnHealthStatus.Repaired,
        Actions = actions ?? Array.Empty<string>(),
    };

    /// <summary>Creates a failed result with an actionable error.</summary>
    public static PreSpawnHealthResult Failed(string error, IReadOnlyList<string>? actions = null) => new()
    {
        Status = PreSpawnHealthStatus.Failed,
        Error = error,
        Actions = actions ?? Array.Empty<string>(),
    };
}

/// <summary>
/// An optional pre-spawn self-heal for a known, recoverable environment issue (e.g.
/// Claude's half-installed npm shim). Null on a descriptor means "always healthy".
/// </summary>
public delegate Task<PreSpawnHealthResult> PreSpawnHealth(PreSpawnHealthContext context, CancellationToken ct);

/// <summary>
/// The one per-CLI value a consumer resolves by type and <em>uses</em> — a record of
/// pure data + delegates, with no base class, no vtable and nothing to override. Each
/// member is a seam that used to be a <c>protected virtual</c> on the driver base; a
/// new CLI is one of these registered in an <see cref="ICliCatalog"/>, not a subclass.
/// </summary>
public sealed record CliDescriptor
{
    /// <summary>The CLI this descriptor drives (one of <see cref="CliTypes"/>).</summary>
    public required string CliType { get; init; }

    /// <summary>Resolves the executable path/command from consumer options.</summary>
    public required Func<CliOptions, string> GetCliPath { get; init; }

    /// <summary>Builds the immutable launch spec for a run (argv, stdin, executable).</summary>
    public required LaunchSpecBuilder BuildLaunch { get; init; }

    /// <summary>Maps raw output lines onto typed events (model-blind).</summary>
    public required CliParser Parse { get; init; }

    /// <summary>Recognises stop conditions per line; the engine maps a verdict onto a <see cref="CliRunEvent.Interrupt"/>. Use <see cref="InterruptClassifiers.None"/> for a CLI with no special grammar.</summary>
    public required IInterruptClassifier InterruptClassifier { get; init; }

    /// <summary>How the engine measures liveness for this CLI (in-band vs a side-channel file).</summary>
    public required LivenessSpec Liveness { get; init; }

    /// <summary>Produces the capability table for a given model.</summary>
    public required CliCapabilitiesProvider Capabilities { get; init; }

    /// <summary>Whether a recorded session id is one this CLI can resume. Defaults to accepting any.</summary>
    public Func<string?, bool> CanResumeSessionId { get; init; } = static _ => true;

    /// <summary>Optional pre-spawn self-heal (e.g. Claude's npm-shim heal); null when the CLI needs none.</summary>
    public PreSpawnHealth? EnsureHealthy { get; init; }

    /// <summary>The clean-context recipe (config-home redirect + seed files), or null when this CLI cannot isolate per-run state.</summary>
    public CleanContextSpec? CleanContext { get; init; }

    /// <summary>
    /// The CLI's agent-definition convention (where project-scoped subagent files
    /// live and how one is rendered), or null when the CLI has no subagent surface
    /// the runner can configure. Delegation itself is the CLI's own behaviour — this
    /// only supplies the definitions.
    /// </summary>
    public Delegation.SubagentSpec? Subagents { get; init; }

    /// <summary>Optional custom availability probe (e.g. Antigravity has no <c>--version</c>); null uses the engine's default <c>--version</c> probe.</summary>
    public Func<CliOptions, string?, (bool Available, string? Version, string Path)>? ProbeCliPath { get; init; }

    /// <summary>Whether this CLI can isolate per-run state — true exactly when <see cref="CleanContext"/> is set.</summary>
    public bool SupportsCleanContext => CleanContext is not null;
}

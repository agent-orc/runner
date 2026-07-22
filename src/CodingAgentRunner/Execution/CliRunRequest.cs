using CodingAgentRunner.Attachments;

namespace CodingAgentRunner.Execution;

/// <summary>
/// Everything the runner needs to start one CLI run. <see cref="RunId"/> is the
/// consumer's correlation key — it threads through every event and the output log.
/// </summary>
public sealed record CliRunRequest
{
    /// <summary>Consumer-assigned correlation id; unique among live runs of one driver.</summary>
    public required string RunId { get; init; }

    /// <summary>The prompt to hand the agent.</summary>
    public required string Prompt { get; init; }

    /// <summary>
    /// Durable attachment references from the chat/task message. The runner resolves
    /// every reference through <see cref="Abstractions.CliOptions.AttachmentResolver"/>
    /// before spawning the CLI and fails the start when any reference is unavailable.
    /// </summary>
    public IReadOnlyList<AttachmentReference>? Attachments { get; init; }

    /// <summary>Working directory the CLI runs in (the checkout / worktree).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Model id to invoke, or null for the CLI's default.</summary>
    public string? Model { get; init; }

    /// <summary>Requested thinking / reasoning level; resolved against the model's capability table.</summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>
    /// CLI-native <c>SessionId</c> to resume — the value captured from a prior run's
    /// <see cref="Events.CliRunEvent.SessionStarted"/>. Null/empty starts a fresh
    /// session. A single id IS the whole resume signal — there is no separate flag.
    /// </summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>Permission posture (one of <see cref="Model.CliPermissionModes"/>); null normalizes to YOLO.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>
    /// Context isolation (one of <see cref="Model.CliContextModes"/>). <b>Defaults to
    /// <c>clean</c></b> — an isolated per-run config home, so a run sees only the
    /// prompt + the versioned repo files. Set to <c>shared</c> to use the operator's
    /// global CLI state. CLIs that cannot isolate (Gemini) run shared regardless.
    /// </summary>
    public string ContextMode { get; init; } = CodingAgentRunner.Model.CliContextModes.Clean;

    /// <summary>
    /// Extra environment variables for this run, applied <b>after</b> the standard
    /// hardening — so they <em>win</em> over it. This is a deliberate escape hatch; use
    /// it to add run-specific vars, not to undo hardening (overriding e.g. the UTF-8 /
    /// no-color / build-server keys can reintroduce the footguns the library prevents).
    /// </summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }

    /// <summary>
    /// CLI-specific tuning knobs as a forward-compatible string bag. Each driver reads
    /// only the keys it understands and ignores the rest — so a new per-CLI knob never
    /// changes this type. Today <b>Codex</b> applies each entry as a <c>-c key=value</c>
    /// config override (its native mechanism); the other CLIs ignore the bag until they
    /// grow a knob. Null/empty means "use the CLI defaults". Reasoning effort is NOT a
    /// tuning key; it has its own first-class field, <see cref="ThinkingLevel"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Tuning { get; init; }
}

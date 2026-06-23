namespace CodingAgentRunner.Execution;

/// <summary>
/// Everything the runner needs to start one CLI run. <see cref="RunId"/> is the
/// consumer's correlation key — it threads through every event and the output log.
/// </summary>
public sealed record CliRunRequest
{
    /// <summary>Consumer-assigned correlation id; unique among live runs of one backend.</summary>
    public required string RunId { get; init; }

    /// <summary>The prompt to hand the agent.</summary>
    public required string Prompt { get; init; }

    /// <summary>Working directory the CLI runs in (the checkout / worktree).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Model id to invoke, or null for the CLI's default.</summary>
    public string? Model { get; init; }

    /// <summary>Requested thinking / reasoning level; resolved against the model's capability table.</summary>
    public string? ThinkingLevel { get; init; }

    /// <summary>CLI-native session id to resume (used only when <see cref="ResumeSession"/> is true).</summary>
    public string? SessionName { get; init; }

    /// <summary>Whether to resume <see cref="SessionName"/> rather than start a fresh session.</summary>
    public bool ResumeSession { get; init; }

    /// <summary>Permission posture (one of <see cref="Model.CliPermissionModes"/>); null normalizes to YOLO.</summary>
    public string? PermissionMode { get; init; }

    /// <summary>Context isolation (one of <see cref="Model.CliContextModes"/>); null normalizes to clean.</summary>
    public string? ContextMode { get; init; }

    /// <summary>Optional file appended to the system prompt (Claude only); ignored by CLIs without the flag.</summary>
    public string? SystemPromptFile { get; init; }

    /// <summary>Extra environment variables for this run, applied after the standard hardening.</summary>
    public IReadOnlyDictionary<string, string>? ExtraEnvironment { get; init; }
}

using CodingAgentRunner.Events;
using CodingAgentRunner.Model;
using CodingAgentRunner.Attachments;
using Microsoft.Extensions.Logging;

namespace CodingAgentRunner.Execution;

/// <summary>
/// An immutable, fully-resolved description of how to spawn one CLI process — the
/// replacement for the mutable <c>ProcessStartInfo</c> a driver builds today. A
/// <see cref="LaunchSpecBuilder"/> produces it from a <see cref="CliLaunchContext"/>;
/// the engine consumes it to spawn, and nothing mutates it after.
/// </summary>
public sealed record LaunchSpec
{
    /// <summary>The resolved executable (absolute path or a command on PATH).</summary>
    public required string Executable { get; init; }

    /// <summary>The argument vector, already split — never a single joined command string.</summary>
    public IReadOnlyList<string> Argv { get; init; } = [];

    /// <summary>The working directory to spawn in (the checkout / worktree).</summary>
    public required string WorkingDirectory { get; init; }

    /// <summary>Process environment overrides for this run, applied after hardening so they win.</summary>
    public IReadOnlyDictionary<string, string> EnvironmentOverrides { get; init; }
        = new Dictionary<string, string>();

    /// <summary>The prompt to write to the child's stdin, or null when the CLI takes the prompt as an argument.</summary>
    public string? StdinPayload { get; init; }

    /// <summary>The model id actually selected (post-normalization), for telemetry / the <c>RunStarted</c> event. Null = the CLI default.</summary>
    public string? NormalizedModel { get; init; }
}

/// <summary>
/// Everything a <see cref="LaunchSpecBuilder"/> needs to build a <see cref="LaunchSpec"/>:
/// the consumer's request plus the values the engine has already resolved (the
/// executable path, and the normalized model / thinking level). Keeps the builder a
/// pure function of its inputs.
/// </summary>
/// <param name="Request">The original run request.</param>
/// <param name="CliPath">The configured executable path/command for this CLI (the builder resolves it to a real binary).</param>
/// <param name="ResolvedModel">The normalized model id, or null for the CLI default.</param>
/// <param name="ResolvedThinkingLevel">The normalized thinking level, or null when the CLI/model has no selector.</param>
/// <param name="Logger">Diagnostics logger (e.g. for a binary-shim rewrite note).</param>
public sealed record CliLaunchContext(
    CliRunRequest Request,
    string CliPath,
    string? ResolvedModel,
    string? ResolvedThinkingLevel,
    ILogger Logger)
{
    /// <summary>
    /// Attachment files resolved and validated before launch. Built-in descriptors
    /// use this for CLI-native image input where available.
    /// </summary>
    public IReadOnlyList<ResolvedAttachment> Attachments { get; init; } = [];
}

/// <summary>Builds the immutable <see cref="LaunchSpec"/> for a run — the descriptor seam that replaces a driver's <c>BuildStartInfo</c> + <c>GetPromptStdinPayload</c>.</summary>
public delegate LaunchSpec LaunchSpecBuilder(CliLaunchContext context);

/// <summary>
/// Maps one raw output line onto zero or more typed <see cref="CliRunEvent"/>s — the
/// descriptor seam that replaces a driver's <c>MapLineToRunEvents</c>. Model-blind and
/// total: any line yields events (possibly just <see cref="CliRunEvent.Unknown"/>),
/// never an exception.
/// </summary>
public delegate IEnumerable<CliRunEvent> CliParser(string rawLine, string runId, CliStreamKind stream);

using CodingAgentRunner.Model;

namespace CodingAgentRunner.Execution;

/// <summary>
/// A narrowly supported launch addition. This is not a raw argument-vector escape
/// hatch: the runner validates every extension and renders its CLI syntax itself.
/// </summary>
public sealed record CliLaunchExtension
{
    /// <summary>The supported launch addition to apply.</summary>
    public required CliLaunchExtensionKind Kind { get; init; }

    /// <summary>The single value consumed by <see cref="Kind"/>.</summary>
    public required string Value { get; init; }

    /// <summary>
    /// Adds Claude Code's <c>--append-system-prompt-file</c> option. The path must
    /// be absolute and name an existing file when the run starts.
    /// </summary>
    public static CliLaunchExtension AppendClaudeSystemPromptFile(string path) => new()
    {
        Kind = CliLaunchExtensionKind.ClaudeAppendSystemPromptFile,
        Value = path,
    };
}

/// <summary>Launch additions the built-in runners explicitly support.</summary>
public enum CliLaunchExtensionKind
{
    /// <summary>Claude Code's <c>--append-system-prompt-file &lt;absolute-path&gt;</c>.</summary>
    ClaudeAppendSystemPromptFile,
}

internal static class CliLaunchExtensions
{
    public static string? Validate(string cliType, IReadOnlyList<CliLaunchExtension>? extensions)
    {
        if (extensions is null || extensions.Count == 0) return null;

        if (cliType != CliTypes.Claude)
            return $"Launch extensions are not supported by {cliType}.";
        if (extensions.Count != 1)
            return "Claude accepts at most one append-system-prompt-file launch extension.";

        var extension = extensions[0];
        if (extension is null || extension.Kind != CliLaunchExtensionKind.ClaudeAppendSystemPromptFile)
            return "The requested launch extension is not supported by Claude.";
        if (string.IsNullOrWhiteSpace(extension.Value) || extension.Value.IndexOf('\0') >= 0)
            return "Claude append-system-prompt-file requires a non-empty path value.";
        if (!Path.IsPathFullyQualified(extension.Value))
            return "Claude append-system-prompt-file requires an absolute path.";
        if (!File.Exists(extension.Value))
            return $"Claude append-system-prompt-file does not exist: '{extension.Value}'.";

        return null;
    }
}

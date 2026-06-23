namespace CodingAgentRunner.Model;

/// <summary>
/// Identifiers for the supported CLI backends. The string values are stable and
/// safe to persist or use as URL/route segments.
/// </summary>
public static class CliTypes
{
    /// <summary>GitHub Copilot CLI.</summary>
    public const string Copilot = "copilot";
    /// <summary>Anthropic Claude Code CLI.</summary>
    public const string Claude  = "claude";
    /// <summary>OpenAI Codex CLI.</summary>
    public const string Codex   = "codex";
    /// <summary>Google Gemini CLI.</summary>
    public const string Gemini  = "gemini";

    /// <summary>
    /// Sentinel for "no automated CLI resolver" (e.g. a router fallback that needs
    /// a human). Deliberately NOT part of <see cref="All"/> / <see cref="IsValid"/> —
    /// it is a comparison sentinel, not a selectable backend.
    /// </summary>
    public const string Human   = "human";

    /// <summary>All selectable CLI backends.</summary>
    public static readonly string[] All = [Copilot, Claude, Codex, Gemini];

    /// <summary>True when <paramref name="type"/> is one of the selectable backends.</summary>
    public static bool IsValid(string? type) =>
        !string.IsNullOrWhiteSpace(type) && All.Contains(type, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonicalize a CLI type; unknown / empty values fall back to <see cref="Copilot"/>.</summary>
    public static string Normalize(string? type) =>
        IsValid(type) ? type!.ToLowerInvariant() : Copilot;
}

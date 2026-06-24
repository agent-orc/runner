namespace CodingAgentRunner.Model;

/// <summary>
/// Identifiers for the supported CLI drivers. The string values are stable and
/// safe to persist or use as URL/route segments.
/// </summary>
public static class CliTypes
{
    /// <summary>Anthropic Claude Code CLI.</summary>
    public const string Claude  = "claude";
    /// <summary>OpenAI Codex CLI.</summary>
    public const string Codex   = "codex";
    /// <summary>Google Gemini CLI. <b>Deprecated</b> — superseded by <see cref="Antigravity"/>.</summary>
    public const string Gemini  = "gemini";

    /// <summary>
    /// Google Antigravity CLI (<c>agentapi</c>) — the maintained Google integration.
    /// Registered as a driver, but deliberately <b>not yet in <see cref="All"/></b>
    /// (kept out of the selectable set until the consumer migrates onto it).
    /// </summary>
    public const string Antigravity = "antigravity";

    /// <summary>
    /// Sentinel for "no automated CLI resolver" (e.g. a router fallback that needs
    /// a human). Deliberately NOT part of <see cref="All"/> / <see cref="IsValid"/> —
    /// it is a comparison sentinel, not a selectable driver.
    /// </summary>
    public const string Human   = "human";

    /// <summary>All selectable CLI drivers.</summary>
    public static readonly string[] All = [Claude, Codex, Gemini];

    /// <summary>True when <paramref name="type"/> is one of the selectable drivers.</summary>
    public static bool IsValid(string? type) =>
        !string.IsNullOrWhiteSpace(type) && All.Contains(type, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonicalize a CLI type; unknown / empty values fall back to <see cref="Claude"/>.</summary>
    public static string Normalize(string? type) =>
        IsValid(type) ? type!.ToLowerInvariant() : Claude;
}

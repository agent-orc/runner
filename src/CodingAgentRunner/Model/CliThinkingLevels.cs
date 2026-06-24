namespace CodingAgentRunner.Model;

/// <summary>
/// Capability table for CLI thinking / reasoning levels. Empty levels mean the
/// CLI/model has no supported selector and the runner should omit any flag.
/// </summary>
public static class CliThinkingLevels
{
    /// <summary>Lowest reasoning effort (OpenAI only).</summary>
    public const string Minimal = "minimal";
    /// <summary>Low reasoning effort.</summary>
    public const string Low = "low";
    /// <summary>Medium reasoning effort.</summary>
    public const string Medium = "medium";
    /// <summary>High reasoning effort.</summary>
    public const string High = "high";
    /// <summary>Extra-high reasoning effort (newer models only).</summary>
    public const string XHigh = "xhigh";
    /// <summary>Maximum reasoning effort (select Claude models only).</summary>
    public const string Max = "max";

    private static readonly IReadOnlyList<string> OpenAiLevels = [Minimal, Low, Medium, High];
    private static readonly IReadOnlyList<string> OpenAiXHighLevels = [Minimal, Low, Medium, High, XHigh];
    private static readonly IReadOnlyList<string> ClaudeBasicLevels = [Low, Medium, High];
    private static readonly IReadOnlyList<string> ClaudeMaxLevels = [Low, Medium, High, Max];
    private static readonly IReadOnlyList<string> ClaudeXHighMaxLevels = [Low, Medium, High, XHigh, Max];

    /// <summary>The thinking / reasoning levels the given CLI + model supports. Empty means no selector.</summary>
    public static IReadOnlyList<string> For(string? cliType, string? model)
    {
        var cli = CliTypes.Normalize(cliType);
        var m = (model ?? string.Empty).Trim();

        if (string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
        {
            if (IsForeignCodexModel(m)) return [];
            return IsXHighCapableCodexModel(m) ? OpenAiXHighLevels : OpenAiLevels;
        }

        if (string.Equals(cli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase))
        {
            var n = m.Replace('.', '-').ToLowerInvariant();
            // Only a real Claude model id (claude-…) gets a ladder. This gate rejects a
            // substring false-positive ("my-opus-4-8-clone") and a malformed id with
            // internal whitespace ("claude - opus - 4 - 8") — both fall back to no ladder.
            if (!n.StartsWith("claude-", StringComparison.Ordinal)) return [];
            if (n.Contains("haiku-4-5", StringComparison.Ordinal)) return [];
            if (n.Contains("opus-4-8", StringComparison.Ordinal)
                || n.Contains("opus-4-7", StringComparison.Ordinal))
                return ClaudeXHighMaxLevels;
            if (n.Contains("opus-4-6", StringComparison.Ordinal)
                || n.Contains("opus-4-5", StringComparison.Ordinal))
                return ClaudeMaxLevels;
            if (n.Contains("sonnet-4-6", StringComparison.Ordinal)) return ClaudeBasicLevels;
            if (n.StartsWith("claude-opus-", StringComparison.Ordinal)) return ClaudeMaxLevels;
            if (n.StartsWith("claude-sonnet-", StringComparison.Ordinal)) return ClaudeBasicLevels;
            return [];
        }

        return [];
    }

    /// <summary>The default thinking level for the given CLI + model, or null when there is no selector.</summary>
    public static string? DefaultFor(string? cliType, string? model)
    {
        var levels = For(cliType, model);
        if (levels.Count == 0) return null;
        return string.Equals(CliTypes.Normalize(cliType), CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
            ? Medium
            : High;
    }

    /// <summary>Resolve a requested level against what the CLI + model supports, falling back to the default.</summary>
    public static string? Normalize(string? cliType, string? model, string? requested)
    {
        var levels = For(cliType, model);
        if (levels.Count == 0) return null;
        var value = string.IsNullOrWhiteSpace(requested)
            ? DefaultFor(cliType, model)
            : requested.Trim().ToLowerInvariant();
        if (value is null) return null;
        return levels.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? levels.First(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))
            : DefaultFor(cliType, model);
    }

    private static bool IsForeignCodexModel(string model)
    {
        // Normalize dots→dashes first so "claude.opus.4.8" is still recognized as foreign.
        var n = model.Replace('.', '-').ToLowerInvariant();
        return n.StartsWith("claude-", StringComparison.Ordinal)
               || n.StartsWith("gemini-", StringComparison.Ordinal);
    }

    /// <summary>
    /// Codex exposes the "Extra High" (<c>xhigh</c>) reasoning effort only on newer
    /// OpenAI models (gpt-5.5 and later). The codex <c>ReasoningEffort</c> enum
    /// serializes to lowercase, so the selector maps directly to
    /// <c>model_reasoning_effort="xhigh"</c>. Older codex models (gpt-5, gpt-5-codex)
    /// top out at <c>high</c>.
    /// </summary>
    private static bool IsXHighCapableCodexModel(string model)
    {
        var m = model.Replace('.', '-').ToLowerInvariant();
        return m.Contains("gpt-5-5", StringComparison.Ordinal)   // gpt-5.5
               || m.Contains("gpt-6", StringComparison.Ordinal)
               || m.Contains("gpt-7", StringComparison.Ordinal);
    }
}

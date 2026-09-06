namespace CodingAgentRunner.Model;

/// <summary>
/// Capability table for CLI thinking / reasoning levels. Empty levels mean the
/// CLI/model has no supported selector and the runner should omit any flag.
/// </summary>
public static class CliThinkingLevels
{
    /// <summary>
    /// Legacy lowest reasoning effort. Kept as a value constant for compatibility;
    /// current Codex model metadata no longer offers it.
    /// </summary>
    public const string Minimal = "minimal";
    /// <summary>Low reasoning effort.</summary>
    public const string Low = "low";
    /// <summary>Medium reasoning effort.</summary>
    public const string Medium = "medium";
    /// <summary>High reasoning effort.</summary>
    public const string High = "high";
    /// <summary>Extra-high reasoning effort (newer models only).</summary>
    public const string XHigh = "xhigh";
    /// <summary>Maximum reasoning effort (select Claude and Codex models).</summary>
    public const string Max = "max";
    /// <summary>
    /// Ultra reasoning effort — the top Codex rung, above <see cref="Max"/>
    /// (newest Codex models only, including gpt-5.6 and gpt-6).
    /// </summary>
    public const string Ultra = "ultra";

    private static readonly IReadOnlyList<string> OpenAiLevels = [Low, Medium, High];
    private static readonly IReadOnlyList<string> OpenAiXHighLevels = [Low, Medium, High, XHigh];
    private static readonly IReadOnlyList<string> OpenAiMaxLevels = [Low, Medium, High, XHigh, Max];
    private static readonly IReadOnlyList<string> OpenAiUltraLevels = [Low, Medium, High, XHigh, Max, Ultra];
    private static readonly IReadOnlyList<string> ClaudeBasicLevels = [Low, Medium, High];
    private static readonly IReadOnlyList<string> ClaudeMaxLevels = [Low, Medium, High, Max];
    private static readonly IReadOnlyList<string> ClaudeXHighMaxLevels = [Low, Medium, High, XHigh, Max];

    /// <summary>The thinking / reasoning levels the given CLI + model supports. Empty means no selector.</summary>
    public static IReadOnlyList<string> For(string? cliType, string? model)
        => For(cliType, model, discoveredCatalog: null);

    /// <summary>
    /// The thinking levels for a CLI + model, preferring a ladder reported by live
    /// discovery and falling back to the static table only when discovery has no
    /// ladder metadata for that model.
    /// </summary>
    public static IReadOnlyList<string> For(
        string? cliType,
        string? model,
        CliModelCatalog? discoveredCatalog)
    {
        var cli = CliTypes.Normalize(cliType);
        var m = (model ?? string.Empty).Trim();

        var discovered = FindDiscovered(cli, m, discoveredCatalog);
        if (discovered?.ThinkingLevelsDiscovered == true)
            return discovered.ThinkingLevels;

        if (string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
        {
            if (IsForeignCodexModel(m)) return [];
            if (IsGpt56Luna(m)) return OpenAiMaxLevels;
            if (IsUltraCapableCodexModel(m)) return OpenAiUltraLevels;
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
            // Sonnet 5 supports the full ladder including xhigh and max (per the
            // Claude Code 2.1.x model metadata); the substring also covers point
            // releases like sonnet-5-5. Note "sonnet-4-5" does NOT match this.
            if (n.Contains("sonnet-5", StringComparison.Ordinal)) return ClaudeXHighMaxLevels;
            if (n.Contains("sonnet-4-6", StringComparison.Ordinal)) return ClaudeBasicLevels;
            if (n.StartsWith("claude-opus-", StringComparison.Ordinal)) return ClaudeMaxLevels;
            if (n.StartsWith("claude-sonnet-", StringComparison.Ordinal)) return ClaudeBasicLevels;
            return [];
        }

        return [];
    }

    /// <summary>
    /// Short human label for a level id (for a UI ladder or probe response). Unknown
    /// ids are echoed back trimmed so a new rung is never silently dropped from a UI.
    /// </summary>
    public static string DisplayName(string? level) => (level ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        Minimal => "Minimal",
        Low => "Low",
        Medium => "Medium",
        High => "High",
        XHigh => "Extra High",
        Ultra => "Ultra",
        Max => "Max",
        _ => (level ?? string.Empty).Trim(),
    };

    /// <summary>The default thinking level for the given CLI + model, or null when there is no selector.</summary>
    public static string? DefaultFor(string? cliType, string? model)
        => DefaultFor(cliType, model, discoveredCatalog: null);

    /// <summary>Resolve the default from live discovery when known, otherwise from the static table.</summary>
    public static string? DefaultFor(
        string? cliType,
        string? model,
        CliModelCatalog? discoveredCatalog)
    {
        var cli = CliTypes.Normalize(cliType);
        var discovered = FindDiscovered(cli, (model ?? string.Empty).Trim(), discoveredCatalog);
        if (discovered?.ThinkingLevelsDiscovered == true)
        {
            if (discovered.ThinkingLevels.Count == 0) return null;
            var discoveredDefault = discovered.DefaultThinkingLevel;
            return discoveredDefault is not null
                   && discovered.ThinkingLevels.Contains(discoveredDefault, StringComparer.OrdinalIgnoreCase)
                ? discovered.ThinkingLevels.First(level =>
                    string.Equals(level, discoveredDefault, StringComparison.OrdinalIgnoreCase))
                : null;
        }

        var levels = For(cliType, model, discoveredCatalog);
        if (levels.Count == 0) return null;
        return string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
            ? Medium
            : High;
    }

    /// <summary>Resolve a requested level against what the CLI + model supports, falling back to the default.</summary>
    public static string? Normalize(string? cliType, string? model, string? requested)
        => Normalize(cliType, model, requested, discoveredCatalog: null);

    /// <summary>Resolve a requested level using a live discovered ladder when one is known.</summary>
    public static string? Normalize(
        string? cliType,
        string? model,
        string? requested,
        CliModelCatalog? discoveredCatalog)
    {
        var levels = For(cliType, model, discoveredCatalog);
        if (levels.Count == 0) return null;
        var value = string.IsNullOrWhiteSpace(requested)
            ? DefaultFor(cliType, model, discoveredCatalog)
            : requested.Trim().ToLowerInvariant();
        if (value is null) return null;
        return levels.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? levels.First(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase))
            : DefaultFor(cliType, model, discoveredCatalog);
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
    /// OpenAI models (gpt-5.3 Codex Spark and later). The Codex reasoning-effort enum
    /// serializes to lowercase, so the selector maps directly to
    /// <c>model_reasoning_effort="xhigh"</c>. Older codex models (gpt-5, gpt-5-codex)
    /// top out at <c>high</c>. Every ultra-capable model is also xhigh-capable.
    /// </summary>
    private static bool IsXHighCapableCodexModel(string model)
    {
        var m = model.Replace('.', '-').ToLowerInvariant();
        return m.Contains("gpt-5-3-codex-spark", StringComparison.Ordinal)
               || m.Contains("gpt-5-4-mini", StringComparison.Ordinal)
               || m.Contains("gpt-5-5", StringComparison.Ordinal)
               || m.Contains("gpt-5-6", StringComparison.Ordinal)
               || m.Contains("gpt-6", StringComparison.Ordinal)
               || m.Contains("gpt-7", StringComparison.Ordinal);
    }

    /// <summary>
    /// Codex exposes the top <c>ultra</c> reasoning effort on its newest family, the
    /// gpt-5.6 models and gpt-6. LIVE metadata from Codex supplies the exact ladder;
    /// this table is used before discovery or when discovery is unavailable.
    /// The gpt-5.6 Luna variant stops at <c>max</c>; other 5.6 variants include
    /// <c>ultra</c> unless live metadata says otherwise.
    /// </summary>
    private static bool IsUltraCapableCodexModel(string model)
    {
        var m = model.Replace('.', '-').ToLowerInvariant();
        return (m.Contains("gpt-5-6", StringComparison.Ordinal) && !IsGpt56Luna(model))
               || m.Contains("gpt-6", StringComparison.Ordinal)
               || m.Contains("gpt-7", StringComparison.Ordinal);
    }

    private static bool IsGpt56Luna(string model)
        => model.Replace('.', '-').Contains("gpt-5-6-luna", StringComparison.OrdinalIgnoreCase);

    private static CliModelInfo? FindDiscovered(
        string cliType,
        string model,
        CliModelCatalog? discoveredCatalog)
        => discoveredCatalog is not null
           && string.Equals(discoveredCatalog.CliType, cliType, StringComparison.OrdinalIgnoreCase)
            ? discoveredCatalog.Find(model)
            : null;
}

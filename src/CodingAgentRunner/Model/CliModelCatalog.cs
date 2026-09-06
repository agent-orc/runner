namespace CodingAgentRunner.Model;

/// <summary>A service tier reported by a CLI for one model.</summary>
public sealed record CliModelServiceTier
{
    /// <summary>Stable tier identifier passed to the CLI or service.</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-friendly tier name.</summary>
    public string Name { get; init; } = "";
    /// <summary>CLI-provided explanation of the tier.</summary>
    public string? Description { get; init; }
}

/// <summary>One entry in a CLI's model catalog, as produced by model discovery.</summary>
public record CliModelInfo
{
    /// <summary>Model identifier passed to <c>--model &lt;id&gt;</c>.</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-friendly label shown in dropdowns. Defaults to <c>Id</c> when empty.</summary>
    public string Label { get; init; } = "";
    /// <summary>CLI-provided description, when available.</summary>
    public string? Description { get; init; }
    /// <summary>Optional premium-request multiplier; null when the CLI has no such notion.</summary>
    public double? Multiplier { get; init; }
    /// <summary>Optional vendor / family grouping (anthropic, openai, google, …).</summary>
    public string? Vendor { get; init; }
    /// <summary>Maximum context window in tokens, when known.</summary>
    public int? ContextWindow { get; init; }
    /// <summary>Other identifiers that resolve to this known model.</summary>
    public List<string> Aliases { get; init; } = [];
    /// <summary>Registry ordering across model generations. Larger values are newer.</summary>
    public int? GenerationOrder { get; init; }
    /// <summary>CLI-provided ordering. Lower values are offered first.</summary>
    public int? Priority { get; init; }
    /// <summary>CLI-provided visibility value.</summary>
    public string? Visibility { get; init; }
    /// <summary>Marks the entry the CLI uses by default when <c>--model</c> is omitted.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Supported thinking / reasoning levels for this model. Empty means no selector.</summary>
    public List<string> ThinkingLevels { get; init; } = [];
    /// <summary>
    /// True when <see cref="ThinkingLevels"/> came from live CLI metadata. This
    /// distinguishes a discovered empty ladder from the absence of discovery data.
    /// </summary>
    public bool ThinkingLevelsDiscovered { get; init; }
    /// <summary>Default thinking / reasoning level for this model, or null when unsupported.</summary>
    public string? DefaultThinkingLevel { get; init; }
    /// <summary>Additional speed tiers reported by the CLI.</summary>
    public List<string> AdditionalSpeedTiers { get; init; } = [];
    /// <summary>Service tiers reported by the CLI.</summary>
    public List<CliModelServiceTier> ServiceTiers { get; init; } = [];
    /// <summary>Whether this model should be offered for new work.</summary>
    public bool Available { get; init; } = true;
    /// <summary>Known but no longer preferred for new work.</summary>
    public bool Deprecated { get; init; }
    /// <summary>Short note explaining why an entry is unavailable or metadata-light.</summary>
    public string? AvailabilityNote { get; init; }
}

/// <summary>The result of discovering a CLI's available models.</summary>
public record CliModelCatalog
{
    /// <summary>The CLI this catalog describes.</summary>
    public string CliType { get; init; } = "";
    /// <summary>The discovered models.</summary>
    public List<CliModelInfo> Models { get; init; } = [];
    /// <summary>How the catalog was obtained: <c>cli</c>, <c>known-models</c>, …</summary>
    public string Source { get; init; } = "config";
    /// <summary>Installed CLI version used to build the catalog, when available.</summary>
    public string? CliVersion { get; init; }
    /// <summary>UTC timestamp of the most recent (re)build. Useful for cache diagnostics.</summary>
    public DateTime FetchedAt { get; init; }

    /// <summary>Find a model by id or known alias using case- and dot/dash-insensitive matching.</summary>
    public CliModelInfo? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var key = KnownModels.NormalizeKey(model);
        return Models.FirstOrDefault(entry =>
            KnownModels.NormalizeKey(entry.Id) == key
            || entry.Aliases.Any(alias => KnownModels.NormalizeKey(alias) == key));
    }
}

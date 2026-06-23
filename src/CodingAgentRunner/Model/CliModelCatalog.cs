namespace CodingAgentRunner.Model;

/// <summary>One entry in a CLI's model catalog, as produced by model discovery.</summary>
public record CliModelInfo
{
    /// <summary>Model identifier passed to <c>--model &lt;id&gt;</c>.</summary>
    public string Id { get; init; } = "";
    /// <summary>Human-friendly label shown in dropdowns. Defaults to <c>Id</c> when empty.</summary>
    public string Label { get; init; } = "";
    /// <summary>Premium-request multiplier (Copilot only; null elsewhere).</summary>
    public double? Multiplier { get; init; }
    /// <summary>Optional vendor / family grouping (anthropic, openai, google, …).</summary>
    public string? Vendor { get; init; }
    /// <summary>Marks the entry the CLI uses by default when <c>--model</c> is omitted.</summary>
    public bool IsDefault { get; init; }
    /// <summary>Supported thinking / reasoning levels for this model. Empty means no selector.</summary>
    public List<string> ThinkingLevels { get; init; } = [];
    /// <summary>Default thinking / reasoning level for this model, or null when unsupported.</summary>
    public string? DefaultThinkingLevel { get; init; }
    /// <summary>Whether this model should be offered for new work.</summary>
    public bool Available { get; init; } = true;
    /// <summary>Known but no longer preferred, or not found in live CLI discovery.</summary>
    public bool Deprecated { get; init; }
    /// <summary>Short note explaining why an entry is unavailable or metadata-light.</summary>
    public string? AvailabilityNote { get; init; }
}

/// <summary>The result of discovering a CLI's available models.</summary>
public record CliModelCatalog
{
    /// <summary>The discovered models.</summary>
    public List<CliModelInfo> Models { get; init; } = [];
    /// <summary>How the catalog was obtained: <c>config</c>, <c>cli-pty</c>, <c>hardcoded</c>, …</summary>
    public string Source { get; init; } = "config";
    /// <summary>UTC timestamp of the most recent (re)build. Useful for cache diagnostics.</summary>
    public DateTime FetchedAt { get; init; }
}

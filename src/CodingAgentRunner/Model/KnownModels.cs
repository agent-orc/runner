namespace CodingAgentRunner.Model;

/// <summary>Stable metadata for a model known to the library.</summary>
public sealed record KnownModel
{
    /// <summary>CLI that offers the model.</summary>
    public required string CliType { get; init; }
    /// <summary>Canonical model identifier.</summary>
    public required string Id { get; init; }
    /// <summary>Human-friendly model label.</summary>
    public required string Label { get; init; }
    /// <summary>Model vendor.</summary>
    public required string Vendor { get; init; }
    /// <summary>Maximum context window in tokens.</summary>
    public required int ContextWindow { get; init; }
    /// <summary>Other identifiers that resolve to this model.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];
    /// <summary>Ordering across model generations. Larger values are newer.</summary>
    public required int GenerationOrder { get; init; }
    /// <summary>Known but no longer preferred for new work.</summary>
    public bool Deprecated { get; init; }
}

/// <summary>
/// Library-owned metadata for the Claude and Codex model families understood by
/// the static reasoning table. Availability is deliberately absent: discovery
/// determines whether the installed CLI currently offers each entry.
/// </summary>
public static class KnownModels
{
    private const string Anthropic = "anthropic";
    private const string OpenAi = "openai";
    private const int ClaudeContext = 200_000;
    private const int CodexContext = 272_000;

    private static readonly IReadOnlyList<KnownModel> Entries =
    [
        // Claude families covered by the static reasoning table.
        Claude("claude-sonnet-5", "Claude Sonnet 5", 5_010, ["sonnet"]),
        Claude("claude-opus-4-8", "Claude Opus 4.8", 4_080, ["opus"]),
        Claude("claude-opus-4-7", "Claude Opus 4.7", 4_070),
        Claude("claude-opus-4-6", "Claude Opus 4.6", 4_060),
        Claude("claude-opus-4-5", "Claude Opus 4.5", 4_050, ["claude-opus-4-5-20251101"]),
        Claude("claude-sonnet-4-6", "Claude Sonnet 4.6", 4_046),
        Claude("claude-sonnet-4-5", "Claude Sonnet 4.5", 4_045, ["claude-sonnet-4-5-20250929"]),
        Claude("claude-haiku-4-5", "Claude Haiku 4.5", 4_040, ["haiku", "claude-haiku-4-5-20251001"]),
        Claude("claude-opus-4-1", "Claude Opus 4.1", 4_010, ["claude-opus-4-1-20250805"], deprecated: true),

        // Codex models. Variant slugs stay separate because live discovery can
        // offer them independently and with different reasoning ladders.
        Codex("gpt-6-astra", "GPT-6-Astra", 6_000),
        Codex("gpt-5.6-sol", "GPT-5.6-Sol", 5_603),
        Codex("gpt-5.6-terra", "GPT-5.6-Terra", 5_602),
        Codex("gpt-5.6-luna", "GPT-5.6-Luna", 5_601),
        Codex("gpt-5.6", "GPT-5.6", 5_600),
        Codex("gpt-5.5", "GPT-5.5", 5_500),
        Codex("gpt-5.4-mini", "GPT-5.4-Mini", 5_400),
        new KnownModel
        {
            CliType = CliTypes.Codex,
            Id = "gpt-5.3-codex-spark",
            Label = "GPT-5.3-Codex-Spark",
            Vendor = OpenAi,
            ContextWindow = 128_000,
            GenerationOrder = 5_300,
        },
        Codex("gpt-5-codex", "GPT-5-Codex", 5_010),
        Codex("gpt-5", "GPT-5", 5_000),
    ];

    /// <summary>Every model in registry order (newest generation first within each CLI).</summary>
    public static IReadOnlyList<KnownModel> All => Entries;

    /// <summary>Known models for one CLI in generation order.</summary>
    public static IReadOnlyList<KnownModel> For(string? cliType)
    {
        var cli = NormalizeCliType(cliType);
        return Entries
            .Where(model => string.Equals(model.CliType, cli, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(model => model.GenerationOrder)
            .ToList();
    }

    /// <summary>Find a known model by canonical id or alias.</summary>
    public static KnownModel? Find(string? cliType, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        var cli = NormalizeCliType(cliType);
        var key = NormalizeKey(model);
        return Entries.FirstOrDefault(entry =>
            string.Equals(entry.CliType, cli, StringComparison.OrdinalIgnoreCase)
            && (NormalizeKey(entry.Id) == key
                || entry.Aliases.Any(alias => NormalizeKey(alias) == key)));
    }

    internal static string NormalizeKey(string value)
        => value.Trim().Replace('.', '-').ToLowerInvariant();

    private static string NormalizeCliType(string? cliType)
        => (cliType ?? string.Empty).Trim().ToLowerInvariant();

    private static KnownModel Claude(
        string id,
        string label,
        int generationOrder,
        IReadOnlyList<string>? aliases = null,
        bool deprecated = false) => new()
        {
            CliType = CliTypes.Claude,
            Id = id,
            Label = label,
            Vendor = Anthropic,
            ContextWindow = ClaudeContext,
            Aliases = aliases ?? [],
            GenerationOrder = generationOrder,
            Deprecated = deprecated,
        };

    private static KnownModel Codex(string id, string label, int generationOrder) => new()
    {
        CliType = CliTypes.Codex,
        Id = id,
        Label = label,
        Vendor = OpenAi,
        ContextWindow = CodexContext,
        GenerationOrder = generationOrder,
    };
}

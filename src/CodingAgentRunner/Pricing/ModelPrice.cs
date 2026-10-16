namespace CodingAgentRunner.Pricing;

/// <summary>ISO 4217 currency codes used by the pricing catalog.</summary>
public static class Currencies
{
    /// <summary>United States dollar — the currency every seeded price is quoted in.</summary>
    public const string Usd = "USD";
}

/// <summary>
/// One historical price point for a model, in a single currency, effective from
/// <see cref="ValidFrom"/> (inclusive) until superseded by a later entry. Rates are quoted
/// per one million tokens (per-MTok). The cost of a run is always computed with the entry that
/// was valid at the run's UTC timestamp, so historic entries are never deleted — a price change
/// adds a new entry with a later <see cref="ValidFrom"/> and leaves the old one in place.
/// </summary>
public sealed record ModelPrice
{
    /// <summary>Price per one million input (prompt) tokens.</summary>
    public required decimal InputPerMTok { get; init; }

    /// <summary>Price per one million output (completion) tokens. Providers that bill reasoning tokens do so at the output rate.</summary>
    public required decimal OutputPerMTok { get; init; }

    /// <summary>
    /// Price per one million cache-read (cache-hit) tokens, or null when the model has no separate
    /// cache-read rate. When null, <see cref="ModelPriceCatalog.ComputeCost"/> falls back to the
    /// input rate for cache-read tokens rather than dropping them silently.
    /// </summary>
    public decimal? CacheReadPerMTok { get; init; }

    /// <summary>
    /// Price per one million cache-write (cache-creation) tokens, or null when the model has no
    /// separate cache-write rate. Seeded Anthropic entries use the 5-minute-TTL cache-write rate.
    /// When null, cache-write tokens are billed at the input rate (see <see cref="CacheReadPerMTok"/>).
    /// </summary>
    public decimal? CacheWritePerMTok { get; init; }

    /// <summary>Input-token threshold above which the whole request uses higher rates, when applicable.</summary>
    public long? LongContextThreshold { get; init; }

    /// <summary>Multiplier for all token rates when the prompt exceeds <see cref="LongContextThreshold"/>.</summary>
    public decimal? LongContextRateMultiplier { get; init; }

    /// <summary>ISO 4217 currency code the rates are quoted in. Defaults to <see cref="Currencies.Usd"/>.</summary>
    public string Currency { get; init; } = Currencies.Usd;

    /// <summary>
    /// UTC instant this price took effect, inclusive. A base entry whose real start date is unknown
    /// uses <see cref="DateTime.MinValue"/> — it then applies to any timestamp up to the next entry.
    /// Store as <see cref="DateTimeKind.Utc"/>; resolution compares this against the run's
    /// <c>atUtc</c> by instant.
    /// </summary>
    public DateTime ValidFrom { get; init; }

    /// <summary>Where the number came from (pricing page, release note, catalog import, …). Free text.</summary>
    public string? Source { get; init; }

    /// <summary>Optional human note, e.g. <c>"introductory pricing through 2026-08-31 (UTC)"</c>.</summary>
    public string? Note { get; init; }

    /// <summary>
    /// True when the number is a best-effort placeholder not yet confirmed against an authoritative
    /// source. The cost is still computed with it, but <see cref="CostBreakdown.Unconfirmed"/> is set
    /// so a caller can surface the caveat instead of trusting the figure silently.
    /// </summary>
    public bool Unconfirmed { get; init; }
}

/// <summary>
/// The pricing record for one model: its canonical id, any aliases that resolve to it, an optional
/// vendor grouping, and the full price <see cref="History"/> (entry order does not matter — resolution
/// selects by <see cref="ModelPrice.ValidFrom"/>). An empty history means the model is known but has no
/// confirmed price; a lookup then resolves to <see cref="PriceStatus.NoPriceForDate"/>, never a silent zero.
/// </summary>
public sealed record ModelListing
{
    /// <summary>Canonical model identifier (as passed to <c>--model &lt;id&gt;</c>).</summary>
    public required string ModelId { get; init; }

    /// <summary>Alternate ids that resolve to this listing (dated snapshots, spelling variants, …). Matched case- and dot/dash-insensitively.</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>Optional vendor / family grouping (<c>anthropic</c>, <c>openai</c>, <c>google</c>, …).</summary>
    public string? Vendor { get; init; }

    /// <summary>The model's price history. May be empty when the model is known but not yet priced.</summary>
    public IReadOnlyList<ModelPrice> History { get; init; } = [];

    /// <summary>Optional note explaining the listing — e.g. that pricing is not yet published, or that a tier is deprecated.</summary>
    public string? Note { get; init; }
}

/// <summary>
/// Token counts for a single run or turn, as consumed by <see cref="ModelPriceCatalog.ComputeCost"/>.
/// Every field is a raw non-negative token total (not per-MTok); negative values are treated as zero.
/// </summary>
/// <param name="Input">Fresh (non-cached) input tokens.</param>
/// <param name="Output">Generated output tokens (reasoning tokens are billed as output by the providers that report them).</param>
/// <param name="CacheRead">Cache-read (cache-hit) input tokens.</param>
/// <param name="CacheWrite">Cache-write (cache-creation) input tokens.</param>
public readonly record struct TokenUsage(long Input, long Output, long CacheRead = 0, long CacheWrite = 0);

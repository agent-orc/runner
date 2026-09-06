namespace CodingAgentRunner.Pricing;

/// <summary>
/// Seed data for <see cref="ModelPriceCatalog.Default"/>: known API prices for the Claude 4.x/5 and
/// OpenAI gpt-5.x/gpt-6 families as of the catalog's build. This is the starting point that replaces the
/// hardcoded, partly-null price table that lived in the consumer.
/// </summary>
/// <remarks>
/// <para>
/// Anthropic figures are the published per-MTok input/output rates. Their cache rates follow
/// Anthropic's documented cache economics: <b>cache-read = 0.1x input</b> and
/// <b>cache-write = 1.25x input</b> (the 5-minute-TTL cache-write rate; the 1-hour TTL is 2x and is
/// not modelled here). Every cache rate is therefore derived, not independently sourced.
/// </para>
/// <para>
/// Where a number is not confirmed against an authoritative source it is either marked
/// <see cref="ModelPrice.Unconfirmed"/> or left out entirely (an empty history) rather than invented.
/// The OpenAI entries are listed as known models with no published price yet, so a lookup
/// resolves to <see cref="PriceStatus.NoPriceForDate"/> instead of guessing a rate.
/// </para>
/// </remarks>
internal static class ModelPriceSeed
{
    private const string VendorAnthropic = "anthropic";
    private const string VendorOpenAi = "openai";

    // Published Anthropic pricing, surfaced through the coding-agent-runner reference tables.
    private const string SourceAnthropic = "Anthropic published pricing (cache-read = 0.1x input, cache-write = 5-min-TTL 1.25x input)";

    /// <summary>Anchor for a base entry whose real launch date is unknown: valid for any instant up to the next entry.</summary>
    private static readonly DateTime SinceForever = DateTime.MinValue;

    /// <summary>The introductory Sonnet-5 rates end 2026-08-31 (UTC); standard pricing applies from this instant.</summary>
    private static readonly DateTime Sonnet5StandardFrom = new(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>The seeded listings. Called once by <see cref="ModelPriceCatalog.Default"/>.</summary>
    public static IReadOnlyList<ModelListing> Listings() =>
    [
        // ---- Claude 5 family (Anthropic) ----
        new ModelListing
        {
            ModelId = "claude-fable-5",
            Vendor = VendorAnthropic,
            History = [AnthropicPrice(10.00m, 50.00m, SinceForever)],
        },
        new ModelListing
        {
            ModelId = "claude-mythos-5",
            Vendor = VendorAnthropic,
            Note = "Project Glasswing only; same pricing as Claude Fable 5.",
            History = [AnthropicPrice(10.00m, 50.00m, SinceForever)],
        },
        new ModelListing
        {
            ModelId = "claude-sonnet-5",
            Vendor = VendorAnthropic,
            // A real price change: introductory rates now, standard rates from 2026-09-01 (UTC).
            History =
            [
                AnthropicPrice(2.00m, 10.00m, SinceForever, note: "Introductory pricing, in effect through 2026-08-31 (UTC)."),
                AnthropicPrice(3.00m, 15.00m, Sonnet5StandardFrom, note: "Standard pricing (introductory period ended 2026-08-31)."),
            ],
        },

        // ---- Claude Opus 4.x family (Anthropic) ----
        new ModelListing
        {
            ModelId = "claude-opus-4-8",
            Vendor = VendorAnthropic,
            History = [AnthropicPrice(5.00m, 25.00m, SinceForever)],
        },
        new ModelListing
        {
            ModelId = "claude-opus-4-7",
            Vendor = VendorAnthropic,
            History = [AnthropicPrice(5.00m, 25.00m, SinceForever)],
        },
        new ModelListing
        {
            ModelId = "claude-opus-4-6",
            Vendor = VendorAnthropic,
            History = [AnthropicPrice(5.00m, 25.00m, SinceForever)],
        },
        new ModelListing
        {
            ModelId = "claude-opus-4-5",
            Vendor = VendorAnthropic,
            Aliases = ["claude-opus-4-5-20251101"],
            // Opus-tier rate assumed from 4.6/4.7/4.8; not confirmed against the pricing page for 4.5.
            History = [AnthropicPrice(5.00m, 25.00m, SinceForever, unconfirmed: true, note: "Opus-tier rate assumed; confirm against the Anthropic pricing page.")],
        },
        new ModelListing
        {
            ModelId = "claude-opus-4-1",
            Vendor = VendorAnthropic,
            Aliases = ["claude-opus-4-1-20250805"],
            // Opus 4.1 used a different (higher) tier than 4.5+. No confirmed number here, so it stays unpriced.
            Note = "Deprecated (retires 2026-08-05). Priced on a different, higher tier than Opus 4.5+; number not confirmed here.",
        },

        // ---- Claude Sonnet 4.x family (Anthropic) ----
        new ModelListing
        {
            ModelId = "claude-sonnet-4-6",
            Vendor = VendorAnthropic,
            History = [AnthropicPrice(3.00m, 15.00m, SinceForever)],
        },
        new ModelListing
        {
            ModelId = "claude-sonnet-4-5",
            Vendor = VendorAnthropic,
            Aliases = ["claude-sonnet-4-5-20250929"],
            History = [AnthropicPrice(3.00m, 15.00m, SinceForever, unconfirmed: true, note: "Sonnet-tier rate assumed; confirm against the Anthropic pricing page.")],
        },

        // ---- Claude Haiku 4.5 (Anthropic) ----
        new ModelListing
        {
            ModelId = "claude-haiku-4-5",
            Vendor = VendorAnthropic,
            Aliases = ["claude-haiku-4-5-20251001"],
            History = [AnthropicPrice(1.00m, 5.00m, SinceForever)],
        },

        // ---- OpenAI Codex families — known models, price not yet published here ----
        new ModelListing
        {
            ModelId = "gpt-6-astra",
            Vendor = VendorOpenAi,
            Note = "Pricing not yet published in this catalog; resolves to NoPriceForDate rather than a guessed rate.",
        },
        new ModelListing
        {
            ModelId = "gpt-5.6",
            Vendor = VendorOpenAi,
            Aliases = ["gpt-5.6-sol"],
            Note = "Pricing not yet published in this catalog; resolves to NoPriceForDate rather than a guessed rate.",
        },
        new ModelListing
        {
            ModelId = "gpt-5.5",
            Vendor = VendorOpenAi,
            Note = "Pricing not yet published in this catalog; resolves to NoPriceForDate rather than a guessed rate.",
        },
        new ModelListing
        {
            ModelId = "gpt-5",
            Vendor = VendorOpenAi,
            Note = "Pricing not yet published in this catalog; resolves to NoPriceForDate rather than a guessed rate.",
        },
        new ModelListing
        {
            ModelId = "gpt-5-codex",
            Vendor = VendorOpenAi,
            Note = "Pricing not yet published in this catalog; resolves to NoPriceForDate rather than a guessed rate.",
        },
    ];

    /// <summary>An Anthropic price point with cache rates derived from the documented multipliers.</summary>
    private static ModelPrice AnthropicPrice(decimal input, decimal output, DateTime validFrom, string? note = null, bool unconfirmed = false) => new()
    {
        InputPerMTok = input,
        OutputPerMTok = output,
        CacheReadPerMTok = input * 0.10m,   // documented Anthropic cache-read rate
        CacheWritePerMTok = input * 1.25m,  // documented 5-minute-TTL cache-write rate
        Currency = Currencies.Usd,
        ValidFrom = validFrom,
        Source = SourceAnthropic,
        Note = note,
        Unconfirmed = unconfirmed,
    };
}

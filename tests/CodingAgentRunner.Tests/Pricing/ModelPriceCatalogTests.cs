using CodingAgentRunner.Pricing;
using Xunit;

namespace CodingAgentRunner.Tests.Pricing;

public class ModelPriceCatalogTests
{
    private static readonly DateTime Now = new(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);

    // A small hand-built catalog so the boundary and fallback cases don't depend on seed values.
    private static ModelPriceCatalog Custom() => new(
    [
        new ModelListing
        {
            ModelId = "test-model",
            Vendor = "test",
            History =
            [
                new ModelPrice { InputPerMTok = 1.00m, OutputPerMTok = 2.00m, CacheReadPerMTok = 0.10m, CacheWritePerMTok = 1.25m, ValidFrom = DateTime.MinValue },
                new ModelPrice { InputPerMTok = 4.00m, OutputPerMTok = 8.00m, CacheReadPerMTok = 0.40m, CacheWritePerMTok = 5.00m, ValidFrom = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) },
            ],
        },
        // Priced only from the future — exercises "known model, no price for this date".
        new ModelListing
        {
            ModelId = "future-model",
            History = [new ModelPrice { InputPerMTok = 9.00m, OutputPerMTok = 9.00m, ValidFrom = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc) }],
        },
        // No cache rates — cache tokens must fall back to the input rate, not vanish.
        new ModelListing
        {
            ModelId = "no-cache-model",
            History = [new ModelPrice { InputPerMTok = 10.00m, OutputPerMTok = 20.00m, ValidFrom = DateTime.MinValue }],
        },
        // Known but never priced.
        new ModelListing { ModelId = "unpriced-model", Note = "no price yet" },
    ]);

    [Fact]
    public void ComputeCost_SumsEveryComponent()
    {
        var breakdown = ModelPriceCatalog.Default.ComputeCost(
            "claude-opus-4-8",
            new TokenUsage(Input: 1_000_000, Output: 1_000_000, CacheRead: 1_000_000, CacheWrite: 1_000_000),
            Now);

        Assert.Equal(PriceStatus.Resolved, breakdown.Status);
        Assert.True(breakdown.HasPrice);
        Assert.Equal("USD", breakdown.Currency);
        Assert.Equal(5.00m, breakdown.InputCost);
        Assert.Equal(25.00m, breakdown.OutputCost);
        Assert.Equal(0.50m, breakdown.CacheReadCost);
        Assert.Equal(6.25m, breakdown.CacheWriteCost);
        Assert.Equal(36.75m, breakdown.Total);
        Assert.False(breakdown.Unconfirmed);
    }

    [Fact]
    public void ComputeCost_ScalesByTokenCount()
    {
        // Opus 4.8 input is $5 / MTok → 250k input tokens = $1.25.
        var breakdown = ModelPriceCatalog.Default.ComputeCost("claude-opus-4-8", new TokenUsage(250_000, 0), Now);
        Assert.Equal(1.25m, breakdown.Total);
    }

    [Fact]
    public void ComputeCost_ZeroUsage_IsZeroNotNull_WhenPriced()
    {
        var breakdown = ModelPriceCatalog.Default.ComputeCost("claude-opus-4-8", default, Now);
        Assert.Equal(PriceStatus.Resolved, breakdown.Status);
        Assert.Equal(0m, breakdown.Total);   // priced-but-empty is 0, which is different from "no price" (null)
    }

    [Theory]
    [InlineData("claude-opus-4-8")]
    [InlineData("Claude-Opus-4-8")]   // case-insensitive
    [InlineData("claude-opus-4.8")]   // dot/dash folded
    [InlineData(" claude-opus-4-8 ")] // trimmed
    public void Find_NormalizesLookupKeys(string id)
    {
        var listing = ModelPriceCatalog.Default.Find(id);
        Assert.NotNull(listing);
        Assert.Equal("claude-opus-4-8", listing!.ModelId);
    }

    [Fact]
    public void Find_ResolvesAliases()
    {
        Assert.Equal("claude-haiku-4-5", ModelPriceCatalog.Default.Find("claude-haiku-4-5-20251001")?.ModelId);
        // The catalogue uses gpt-5.6-sol as its canonical id.
        Assert.Equal("gpt-5.6-sol", ModelPriceCatalog.Default.Find("gpt-5.6")?.ModelId);
        Assert.Equal("gpt-5.6-sol", ModelPriceCatalog.Default.Find("gpt-5-6")?.ModelId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("totally-made-up-model")]
    public void UnknownModel_IsExplicit_NeverSilentZero(string? model)
    {
        var resolution = ModelPriceCatalog.Default.ResolvePrice(model, Now);
        Assert.Equal(PriceStatus.UnknownModel, resolution.Status);
        Assert.False(resolution.Found);
        Assert.Null(resolution.Price);

        var breakdown = ModelPriceCatalog.Default.ComputeCost(model, new TokenUsage(1_000, 1_000), Now);
        Assert.Equal(PriceStatus.UnknownModel, breakdown.Status);
        Assert.False(breakdown.HasPrice);
        Assert.Null(breakdown.Total);   // never $0
    }

    [Fact]
    public void KnownButUnpriced_ResolvesToNoPriceForDate_NotZero()
    {
        var breakdown = Custom().ComputeCost("unpriced-model", new TokenUsage(1_000, 1_000), Now);
        Assert.Equal(PriceStatus.NoPriceForDate, breakdown.Status);
        Assert.Equal("unpriced-model", breakdown.ModelId);
        Assert.False(breakdown.HasPrice);
        Assert.Null(breakdown.Total);
    }

    [Fact]
    public void DateBeforeFirstEntry_IsNoPriceForDate()
    {
        var catalog = Custom();
        var before = catalog.ResolvePrice("future-model", Now);
        Assert.Equal(PriceStatus.NoPriceForDate, before.Status);
        Assert.Equal("future-model", before.ModelId);

        var after = catalog.ResolvePrice("future-model", new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal(PriceStatus.Resolved, after.Status);
        Assert.Equal(9.00m, after.Price!.InputPerMTok);
    }

    [Fact]
    public void ResolvePrice_ValidFromBoundaryIsInclusive()
    {
        var catalog = Custom();
        var boundary = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);

        // Exactly at ValidFrom → the newer entry applies (boundary is inclusive).
        Assert.Equal(4.00m, catalog.ResolvePrice("test-model", boundary).Price!.InputPerMTok);
        // One tick earlier → the previous entry still applies.
        Assert.Equal(1.00m, catalog.ResolvePrice("test-model", boundary.AddTicks(-1)).Price!.InputPerMTok);
    }

    [Fact]
    public void ComputeCost_UsesPriceValidAtRunTime()
    {
        var catalog = Custom();
        // 250k input tokens at the old rate ($1) = $0.25; at the new rate ($4) = $1.00.
        var old = catalog.ComputeCost("test-model", new TokenUsage(250_000, 0), new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var current = catalog.ComputeCost("test-model", new TokenUsage(250_000, 0), Now);
        Assert.Equal(0.25m, old.Total);
        Assert.Equal(1.00m, current.Total);
    }

    [Fact]
    public void SeededSonnet5_UsesCataloguePriceAfterSeptemberBoundary()
    {
        var standardFrom = new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc);

        // Before the boundary: introductory $2 / MTok input.
        var intro = ModelPriceCatalog.Default.ResolvePrice("claude-sonnet-5", standardFrom.AddTicks(-1));
        Assert.Equal(2.00m, intro.Price!.InputPerMTok);
        Assert.Equal(10.00m, intro.Price.OutputPerMTok);

        // The dated TokenEconomy snapshot still lists $2 / $10 after this boundary.
        var standard = ModelPriceCatalog.Default.ResolvePrice("claude-sonnet-5", standardFrom);
        Assert.Equal(2.00m, standard.Price!.InputPerMTok);
        Assert.Equal(10.00m, standard.Price.OutputPerMTok);
    }

    [Fact]
    public void CacheCost_FallsBackToInputRate_WhenNoCacheRate()
    {
        var catalog = Custom();
        var breakdown = catalog.ComputeCost("no-cache-model", new TokenUsage(0, 0, CacheRead: 1_000_000, CacheWrite: 1_000_000), Now);
        // input rate is $10 / MTok, applied to both cache-read and cache-write tokens.
        Assert.Equal(10.00m, breakdown.CacheReadCost);
        Assert.Equal(10.00m, breakdown.CacheWriteCost);
        Assert.Equal(20.00m, breakdown.Total);
    }

    [Fact]
    public void Confirmed_Price_SurfacesOnBreakdown()
    {
        var breakdown = ModelPriceCatalog.Default.ComputeCost("claude-opus-4-5", new TokenUsage(1_000_000, 0), Now);
        Assert.Equal(PriceStatus.Resolved, breakdown.Status);
        Assert.False(breakdown.Unconfirmed);
        Assert.Equal(5.00m, breakdown.Total);
    }

    [Fact]
    public void ComputeCost_IsDeterministic()
    {
        var usage = new TokenUsage(123_456, 78_910, 4_096, 2_048);
        var a = ModelPriceCatalog.Default.ComputeCost("claude-opus-4-8", usage, Now);
        var b = ModelPriceCatalog.Default.ComputeCost("claude-opus-4-8", usage, Now);
        Assert.Equal(a, b);   // record value-equality
    }

    [Fact]
    public void NegativeTokenCounts_ContributeNothing()
    {
        var breakdown = ModelPriceCatalog.Default.ComputeCost("claude-opus-4-8", new TokenUsage(-5, -5, -5, -5), Now);
        Assert.Equal(0m, breakdown.Total);
    }

    [Fact]
    public void Constructor_RejectsBlankModelId()
        => Assert.Throws<ArgumentException>(() => new ModelPriceCatalog([new ModelListing { ModelId = "  " }]));

    [Fact]
    public void Constructor_RejectsDuplicateKeys()
        => Assert.Throws<ArgumentException>(() => new ModelPriceCatalog(
        [
            new ModelListing { ModelId = "dup" },
            new ModelListing { ModelId = "DUP" },   // collides after normalization
        ]));
}

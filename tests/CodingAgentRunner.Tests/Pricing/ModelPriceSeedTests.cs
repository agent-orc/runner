using CodingAgentRunner.Pricing;
using Xunit;

namespace CodingAgentRunner.Tests.Pricing;

public class ModelPriceSeedTests
{
    private static readonly ModelPriceCatalog Catalog = ModelPriceCatalog.Default;

    [Fact]
    public void DefaultCatalog_BuildsWithoutDuplicateKeys()
        => Assert.NotEmpty(Catalog.Listings);   // constructor throws on a duplicate id/alias

    [Fact]
    public void EveryListing_HasAnIdAndEveryPricedEntryIsPositive()
    {
        foreach (var listing in Catalog.Listings)
        {
            Assert.False(string.IsNullOrWhiteSpace(listing.ModelId));
            foreach (var price in listing.History)
            {
                // A seeded price never accidentally reads as $0 for input or output.
                Assert.True(price.InputPerMTok > 0m, $"{listing.ModelId} input rate");
                Assert.True(price.OutputPerMTok > 0m, $"{listing.ModelId} output rate");
                if (price.CacheReadPerMTok is { } cr) Assert.True(cr > 0m);
                if (price.CacheWritePerMTok is { } cw) Assert.True(cw > 0m);
                Assert.Equal("USD", price.Currency);
            }
        }
    }

    [Fact]
    public void EveryAlias_ResolvesToItsListing()
    {
        foreach (var listing in Catalog.Listings)
            foreach (var alias in listing.Aliases)
                Assert.Equal(listing.ModelId, Catalog.Find(alias)?.ModelId);
    }

    [Theory]
    [InlineData("claude-fable-5", 10.00, 50.00)]
    [InlineData("claude-opus-4-8", 5.00, 25.00)]
    [InlineData("claude-opus-4-7", 5.00, 25.00)]
    [InlineData("claude-opus-4-6", 5.00, 25.00)]
    [InlineData("claude-sonnet-4-6", 3.00, 15.00)]
    [InlineData("claude-haiku-4-5", 1.00, 5.00)]
    public void ConfirmedClaudeModels_HaveTheKnownRates(string model, double input, double output)
    {
        var price = Catalog.ResolvePrice(model, new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc)).Price;
        Assert.NotNull(price);
        Assert.Equal((decimal)input, price!.InputPerMTok);
        Assert.Equal((decimal)output, price.OutputPerMTok);
        Assert.False(price.Unconfirmed);
        // Anthropic cache rates are the documented multiples of the input rate.
        Assert.Equal((decimal)input * 0.10m, price.CacheReadPerMTok);
        Assert.Equal((decimal)input * 1.25m, price.CacheWritePerMTok);
    }

    [Theory]
    [InlineData("gpt-6-astra")]
    [InlineData("gpt-5.6")]
    [InlineData("gpt-5.5")]
    [InlineData("gpt-5")]
    [InlineData("gpt-5-codex")]
    public void OpenAiModelsWithoutPublishedRates_AreKnownButUnpriced(string model)
    {
        var listing = Catalog.Find(model);
        Assert.NotNull(listing);
        Assert.Equal("openai", listing!.Vendor);
        Assert.Empty(listing.History);   // present, but no invented number
        Assert.NotNull(listing.Note);
    }

    [Fact]
    public void UnconfirmedEntries_AreFlagged_NotInvented()
    {
        // The models we couldn't confirm against an authoritative table carry the flag rather than a silent claim.
        foreach (var id in new[] { "claude-opus-4-5", "claude-sonnet-4-5" })
        {
            var price = Catalog.ResolvePrice(id, new DateTime(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc)).Price;
            Assert.NotNull(price);
            Assert.True(price!.Unconfirmed);
            Assert.NotNull(price.Note);
        }
    }
}

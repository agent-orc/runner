using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using CodingAgentRunner.Pricing;
using Xunit;

namespace CodingAgentRunner.Tests.Pricing;

public class ModelPriceSeedTests
{
    private static readonly ModelPriceCatalog Catalog = ModelPriceCatalog.Default;
    private static readonly string FixtureDirectory = Path.Combine(AppContext.BaseDirectory, "pricing");

    [Fact]
    public void GeneratedSeed_MatchesPinnedTokenEconomySnapshot()
    {
        var sourceBytes = File.ReadAllBytes(Path.Combine(FixtureDirectory, "model-prices.json"));
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(FixtureDirectory, "catalog-source.json")));
        var metadata = manifest.RootElement;
        var hash = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        Assert.Equal(metadata.GetProperty("sha256").GetString(), hash);
        Assert.Equal(ModelPriceSeed.SourceVersion, metadata.GetProperty("catalogVersion").GetString());
        Assert.Equal(ModelPriceSeed.SourceSnapshotDate, metadata.GetProperty("snapshotDate").GetString());
        Assert.Equal(ModelPriceSeed.SourceSha256, hash);

        using var source = JsonDocument.Parse(sourceBytes);
        var expected = source.RootElement.EnumerateArray().ToArray();
        Assert.Equal(expected.Length, Catalog.Listings.Count);
        foreach (var (row, index) in expected.Select((row, index) => (row, index)))
        {
            var listing = Catalog.Listings[index];
            Assert.Equal(row.GetProperty("modelId").GetString(), listing.ModelId);
            Assert.Equal(OptionalString(row, "vendor"), listing.Vendor);
            Assert.Equal(OptionalString(row, "note"), listing.Note);
            var aliases = row.TryGetProperty("aliases", out var aliasRows)
                ? aliasRows.EnumerateArray().Select(alias => alias.GetString()!).ToArray()
                : [];
            Assert.Equal(aliases, listing.Aliases);
            var prices = row.GetProperty("history").EnumerateArray().ToArray();
            Assert.Equal(prices.Length, listing.History.Count);
            foreach (var (priceRow, priceIndex) in prices.Select((priceRow, priceIndex) => (priceRow, priceIndex)))
            {
                var price = listing.History[priceIndex];
                Assert.Equal(DateTime.Parse(priceRow.GetProperty("validFrom").GetString()!, CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal), price.ValidFrom);
                Assert.Equal(priceRow.GetProperty("inputPerMTok").GetDecimal(), price.InputPerMTok);
                Assert.Equal(priceRow.GetProperty("outputPerMTok").GetDecimal(), price.OutputPerMTok);
                Assert.Equal(OptionalDecimal(priceRow, "cacheReadPerMTok"), price.CacheReadPerMTok);
                Assert.Equal(OptionalDecimal(priceRow, "cacheWritePerMTok"), price.CacheWritePerMTok);
                Assert.Equal(OptionalString(priceRow, "currency") ?? "USD", price.Currency);
                Assert.Equal(OptionalString(priceRow, "source"), price.Source);
                Assert.Equal(OptionalString(priceRow, "note"), price.Note);
                Assert.Equal(priceRow.TryGetProperty("unconfirmed", out var unconfirmed) && unconfirmed.GetBoolean(), price.Unconfirmed);
            }
        }
    }

    [Theory]
    [InlineData("gpt-6-sol", "2026-09-22", 2, 0.2, 2.5, 10)]
    [InlineData("gpt-6-luna", "2026-09-22", 0.1, 0.01, 0.125, 0.5)]
    [InlineData("gpt-6.1-sol", "2026-09-29", 2, 0.1, 2.5, 10)]
    [InlineData("claude-opus-5-5", "2026-09-22", 4, 0.2, 5, 20)]
    [InlineData("claude-sonnet-5-5", "2026-09-28", 2, 0.2, 2.5, 10)]
    public void NewModels_ResolveDatedCatalogueRates(string id, string date, double input, double cacheRead, double cacheWrite, double output)
    {
        var from = DateTime.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        Assert.Equal(PriceStatus.NoPriceForDate, Catalog.ResolvePrice(id, from.AddTicks(-1)).Status);
        var price = Catalog.ResolvePrice(id, from).Price;
        Assert.NotNull(price);
        Assert.Equal((decimal)input, price!.InputPerMTok);
        Assert.Equal((decimal)cacheRead, price.CacheReadPerMTok);
        Assert.Equal((decimal)cacheWrite, price.CacheWritePerMTok);
        Assert.Equal((decimal)output, price.OutputPerMTok);
    }

    [Fact]
    public void RemovedUnsupportedModel_DoesNotResolve()
        => Assert.Null(Catalog.Find("claude-mythos-5"));

    [Fact]
    public void DefaultCatalog_BuildsWithoutDuplicateKeys()
        => Assert.NotEmpty(Catalog.Listings);

    [Fact]
    public void EveryAlias_ResolvesToItsListing()
    {
        foreach (var listing in Catalog.Listings)
            foreach (var alias in listing.Aliases)
                Assert.Equal(listing.ModelId, Catalog.Find(alias)?.ModelId);
    }

    private static string? OptionalString(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetString() : null;

    private static decimal? OptionalDecimal(JsonElement value, string key)
        => value.TryGetProperty(key, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.GetDecimal() : null;
}

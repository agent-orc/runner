namespace CodingAgentRunner.Pricing;

/// <summary>
/// The single library of per-model API prices, with history, plus the pure cost API over it.
/// A run's cost is computed with the price that was valid at the run's UTC timestamp
/// (<see cref="ResolvePrice"/> / <see cref="ComputeCost"/>), so this replaces scattered hardcoded
/// price tables with one deterministic, unit-tested source of truth.
/// </summary>
/// <remarks>
/// The catalog is immutable and deterministic: the same inputs always yield the same result, and it
/// has no logging or I/O. Unknown or unpriced models are reported explicitly through
/// <see cref="PriceStatus"/> — never as a silent zero cost. Use <see cref="Default"/> for the seeded
/// catalog, or construct one from your own <see cref="ModelListing"/> set.
/// </remarks>
public sealed class ModelPriceCatalog
{
    private readonly List<ModelListing> _listings;
    private readonly Dictionary<string, ModelListing> _byKey;

    /// <summary>Build a catalog from a set of listings. Ids and aliases are indexed case- and dot/dash-insensitively.</summary>
    /// <exception cref="ArgumentException">A listing has a blank id, or two listings collide on the same normalized id/alias.</exception>
    public ModelPriceCatalog(IEnumerable<ModelListing> listings)
    {
        _listings = [.. listings];
        _byKey = new Dictionary<string, ModelListing>(StringComparer.Ordinal);
        foreach (var listing in _listings)
        {
            if (string.IsNullOrWhiteSpace(listing.ModelId))
                throw new ArgumentException("A ModelListing must have a non-empty ModelId.", nameof(listings));
            Index(listing.ModelId, listing);
            foreach (var alias in listing.Aliases)
                Index(alias, listing);
        }
    }

    private void Index(string key, ModelListing listing)
    {
        var normalized = Normalize(key);
        if (normalized.Length == 0) return;
        if (_byKey.TryGetValue(normalized, out var existing) && !ReferenceEquals(existing, listing))
            throw new ArgumentException($"Duplicate model key '{key}' (normalized '{normalized}').", nameof(listing));
        _byKey[normalized] = listing;
    }

    /// <summary>Every listing in the catalog, in the order supplied. The "list endpoint" for catalogs.</summary>
    public IReadOnlyList<ModelListing> Listings => _listings;

    /// <summary>The seeded catalog: known Claude 4.x/5 and OpenAI gpt-5.x/gpt-6 models. See <see cref="ModelPriceSeed"/>.</summary>
    public static ModelPriceCatalog Default { get; } = new(ModelPriceSeed.Listings());

    /// <summary>Find the listing for a model id or alias, or null if it is not in the catalog. Case- and dot/dash-insensitive.</summary>
    public ModelListing? Find(string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        return _byKey.TryGetValue(Normalize(model), out var listing) ? listing : null;
    }

    /// <summary>
    /// Resolve the price in effect for <paramref name="model"/> at <paramref name="atUtc"/> (a UTC instant).
    /// The chosen entry is the one with the greatest <see cref="ModelPrice.ValidFrom"/> that is
    /// &lt;= <paramref name="atUtc"/> (the boundary is inclusive).
    /// </summary>
    public PriceResolution ResolvePrice(string? model, DateTime atUtc)
    {
        var listing = Find(model);
        if (listing is null)
            return new PriceResolution { Status = PriceStatus.UnknownModel };

        var price = SelectValid(listing.History, atUtc);
        return price is null
            ? new PriceResolution { Status = PriceStatus.NoPriceForDate, ModelId = listing.ModelId }
            : new PriceResolution { Status = PriceStatus.Resolved, ModelId = listing.ModelId, Price = price };
    }

    /// <summary>
    /// Compute the cost of <paramref name="usage"/> for <paramref name="model"/> at <paramref name="atUtc"/>.
    /// Returns a per-component breakdown and a <see cref="CostBreakdown.Total"/>. When the model is unknown
    /// or has no price valid at that time, <see cref="CostBreakdown.Total"/> is null — never a silent zero.
    /// </summary>
    public CostBreakdown ComputeCost(string? model, TokenUsage usage, DateTime atUtc)
    {
        var resolution = ResolvePrice(model, atUtc);
        if (resolution.Price is null)
            return new CostBreakdown { Status = resolution.Status, ModelId = resolution.ModelId, Total = null };

        var price = resolution.Price;
        var input = CostOf(usage.Input, price.InputPerMTok);
        var output = CostOf(usage.Output, price.OutputPerMTok);
        // A missing cache rate falls back to the input rate so cache tokens are still priced, not dropped.
        var cacheRead = CostOf(usage.CacheRead, price.CacheReadPerMTok ?? price.InputPerMTok);
        var cacheWrite = CostOf(usage.CacheWrite, price.CacheWritePerMTok ?? price.InputPerMTok);

        return new CostBreakdown
        {
            Status = PriceStatus.Resolved,
            ModelId = resolution.ModelId,
            Price = price,
            Currency = price.Currency,
            InputCost = input,
            OutputCost = output,
            CacheReadCost = cacheRead,
            CacheWriteCost = cacheWrite,
            Total = input + output + cacheRead + cacheWrite,
        };
    }

    /// <summary>Cost of a token count at a per-MTok rate. Non-positive counts cost nothing.</summary>
    private static decimal CostOf(long tokens, decimal ratePerMTok)
        => tokens <= 0 ? 0m : tokens / 1_000_000m * ratePerMTok;

    /// <summary>The entry with the latest <see cref="ModelPrice.ValidFrom"/> that is not after <paramref name="atUtc"/>, or null.</summary>
    private static ModelPrice? SelectValid(IReadOnlyList<ModelPrice> history, DateTime atUtc)
    {
        ModelPrice? best = null;
        foreach (var entry in history)
        {
            if (entry.ValidFrom <= atUtc && (best is null || entry.ValidFrom > best.ValidFrom))
                best = entry;
        }
        return best;
    }

    /// <summary>Canonicalize a lookup key: trim, lowercase, and fold <c>.</c> to <c>-</c> so <c>gpt-5.6</c> and <c>gpt-5-6</c> match.</summary>
    private static string Normalize(string key)
        => key.Trim().ToLowerInvariant().Replace('.', '-');
}

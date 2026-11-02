# Pricing

`CodingAgentRunner.Pricing` provides a pure cost API and a static price snapshot with
history. A caller can also supply its own listings.

It ships in the core `CodingAgentRunner` package — reference it the same way you reference
`CliThinkingLevels`.

## The model

- A **`ModelListing`** is one model: its canonical id, any aliases that resolve to it, an
  optional vendor, and a price **history**.
- A **`ModelPrice`** is one entry in that history: `InputPerMTok`, `OutputPerMTok`, optional
  `CacheReadPerMTok` / `CacheWritePerMTok`, a `Currency`, a `ValidFrom` UTC instant (inclusive),
  and a `Source` / `Note` / `Unconfirmed` flag. A price can also set a prompt-length threshold and rate multiplier.
- **Prices are kept, not overwritten.** A price change adds a new entry with a later
  `ValidFrom`. The cost of a run is computed with the entry that was valid *at the run's
  timestamp*, so a re-priced run in the past still costs what it cost then.

## The cost API

`ModelPriceCatalog.Default` is the seeded catalog. It uses the TokenEconomy catalogue
snapshot dated 2026-10-16, based on TokenEconomy 0.3.7 with an Anthropic-verified Haiku 5.5 entry. Everything on it is pure
and deterministic.

```csharp
using CodingAgentRunner.Pricing;

var catalog = ModelPriceCatalog.Default;

// What was this model's price at a given time?
PriceResolution p = catalog.ResolvePrice("claude-opus-4-8", DateTime.UtcNow);

// Cost of a run's usage, priced at the run's UTC timestamp.
CostBreakdown cost = catalog.ComputeCost(
    "claude-opus-4-8",
    new TokenUsage(Input: 120_000, Output: 8_000, CacheRead: 40_000),
    runStartedUtc);

if (cost.HasPrice)
    Console.WriteLine($"{cost.Total} {cost.Currency}" + (cost.Unconfirmed ? " (unconfirmed)" : ""));

// List endpoint: every model and its history.
foreach (var listing in catalog.Listings) { /* … */ }
```

You can also build a catalog from your own `ModelListing` set: `new ModelPriceCatalog(listings)`.

## Unknown and unpriced models are explicit — never a silent `$0`

The status is part of the return type, so a missing price can't be mistaken for a free run:

| Situation | `PriceStatus` | `CostBreakdown.Total` |
|-----------|---------------|-----------------------|
| Price found for the timestamp | `Resolved` | the computed total |
| Model id not in the catalog | `UnknownModel` | `null` |
| Model known, but no price valid at/before the timestamp | `NoPriceForDate` | `null` |

`ComputeCost` returns `Total == null` for both non-`Resolved` cases. There is no logging to
consult — the outcome is in the value.

## Lookups

Model ids and aliases resolve case- and dot/dash-insensitively, so `claude-opus-4.8`,
`claude-opus-4-8`, dated snapshots like `claude-haiku-4-5-20251001`, and `gpt-5-6` / `gpt-5.6`
all resolve to their listing.

## Seed data and confidence

The pinned source is [pricing/model-prices.json](../pricing/model-prices.json), copied
from TokenEconomy's `src/TokenEconomy/catalog/model-prices.json`. Its source version,
date, path, and SHA-256 are in
[pricing/catalog-source.json](../pricing/catalog-source.json). The generated
`ModelPriceSeed.cs` carries all supported listing fields and dated price history,
including the source text and confidence flag. The pricing test compares every
generated listing and rate with the pinned JSON and checks its version and hash.
The hash covers the raw bytes, so [.gitattributes](../.gitattributes) keeps
`pricing/*.json` at LF line endings on every checkout, Windows included.

To refresh the seed, copy a newer TokenEconomy catalogue into `pricing/model-prices.json`,
update `pricing/catalog-source.json` from its snapshot index, run
`python3 scripts/generate-model-price-seed.py`, then run
`python3 scripts/generate-model-price-seed.py --check` and the package tests. The
runner keeps this snapshot in its package and has no TokenEconomy runtime dependency.

The seed represents the catalogue's standard API tariff. For example, GPT-6 Sol and
GPT-6.1 Sol have higher long-context rates that this API cannot select from the
token counts alone. Use a host-supplied catalog when the applicable tariff differs.

When an entry omits cache rates, cache-read and cache-write tokens are billed at the input rate
rather than dropped — a documented approximation used only for entries that lack their own cache
figures (the seeded Anthropic entries all carry them).

Claude Haiku 5.5 uses its lower rates through 100,000 prompt tokens. Above that threshold, `ComputeCost` applies the 5× rate to the whole request, including input, output, cache reads, and cache writes. Prompt length includes fresh input and both cache token categories.

For exact Haiku 5.5 costs, call `ComputeCost` per API request. Aggregating several requests before pricing loses which prompts crossed the 100,000-token threshold.

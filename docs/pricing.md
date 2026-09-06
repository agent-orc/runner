# Pricing

`CodingAgentRunner.Pricing` is one library of per-model API prices, with history, plus a
pure cost API over it. It exists so that every token-cost computation goes through a single,
deterministic, unit-tested source instead of a hardcoded price table copied into each caller.

It ships in the core `CodingAgentRunner` package — reference it the same way you reference
`CliThinkingLevels`.

## The model

- A **`ModelListing`** is one model: its canonical id, any aliases that resolve to it, an
  optional vendor, and a price **history**.
- A **`ModelPrice`** is one entry in that history: `InputPerMTok`, `OutputPerMTok`, optional
  `CacheReadPerMTok` / `CacheWritePerMTok`, a `Currency`, a `ValidFrom` UTC instant (inclusive),
  and a `Source` / `Note` / `Unconfirmed` flag.
- **Prices are kept, not overwritten.** A price change adds a new entry with a later
  `ValidFrom`. The cost of a run is computed with the entry that was valid *at the run's
  timestamp*, so a re-priced run in the past still costs what it cost then.

## The cost API

`ModelPriceCatalog.Default` is the seeded catalog. Everything on it is pure and deterministic.

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

The seed covers the Claude 4.x/5 families and the OpenAI gpt-5.x families, plus a
known unpriced listing for `gpt-6-astra`, as a starting point.

- **Anthropic rates** are the published per-MTok input/output figures. Cache rates follow
  Anthropic's documented economics — cache-read = 0.1x input, and cache-write = 1.25x input (the
  5-minute-TTL rate; the 1-hour TTL is 2x and is not modelled) — so they are derived, not
  independently sourced.
- **Numbers that could not be confirmed** against an authoritative source are marked
  `Unconfirmed` (cost is still computed, and `CostBreakdown.Unconfirmed` surfaces the caveat) or
  left unpriced (an empty history → `NoPriceForDate`). The OpenAI entries without a
  published rate are listed as known but unpriced rather than carrying a guessed rate.
  Nothing is invented.

When an entry omits cache rates, cache-read and cache-write tokens are billed at the input rate
rather than dropped — a documented approximation used only for entries that lack their own cache
figures (the seeded Anthropic entries all carry them).

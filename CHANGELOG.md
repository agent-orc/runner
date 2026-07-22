# Changelog

All notable changes to CodingAgentRunner are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/) (pre-1.0: the public API may still shift).

## [0.6.0] - 2026-07-22

### Added

- **Wait-on-Quota**, a per-run library option: on a limit-hit, the runner checks
  the CLI's own quota status and, if the reset falls under a configurable
  threshold, pauses and resumes on the same CLI instead of switching to a
  fallback. Configured via `CliOptions`; the decision itself lives in a small,
  independently testable `QuotaWaitPolicy`.
- **Typed CLI diagnostics** — a new `CliRunEvent` case for warnings and other
  structured diagnostics surfaced on stderr or a CLI's own protocol channel,
  plus richer session and turn metadata (session id propagation, turn usage
  summaries). Repeated diagnostics are coalesced (`DiagnosticCoalescer`) so a
  noisy CLI reports one deduplicated event with a count instead of a flood of
  duplicates. The Codex adapter gained the most surface here, extracting
  plugin- and warning-level detail its protocol already carries.

### Changed

- Repository references (readme, package/repository URLs, release workflow)
  now point at the `agent-orc` GitHub org, following the project's transfer
  and short rename.
- The runner's page on the project website was relaunched with self-hosted
  fonts (Inter, Space Grotesk, JetBrains Mono) instead of a CDN font
  dependency.

## [0.5.0] - 2026-07-10

### Added

- **`CodingAgentRunner.Pricing`** — one library of per-model API prices, with history,
  plus a pure cost API over it. This replaces scattered, hardcoded price tables: every
  token-cost computation goes through one deterministic, unit-tested source.
  - `ModelPriceCatalog` with `ResolvePrice(model, atUtc)`, `ComputeCost(model, usage, atUtc)`,
    `Find(model)`, and a `Listings` "list endpoint". `ModelPriceCatalog.Default` is the seeded
    catalog; you can also build one from your own `ModelListing` set.
  - **Prices have history.** Each model carries a list of `ModelPrice` entries keyed by
    `ValidFrom` (inclusive, UTC). A run's cost is computed with the price valid *at the run's
    timestamp*, so historic entries are kept, not overwritten — e.g. Claude Sonnet 5 seeds its
    introductory rate now and its standard rate from 2026-09-01.
  - **Unknown and unpriced models are explicit, never a silent `$0`.** An unknown id resolves
    to `PriceStatus.UnknownModel`; a known-but-unpriced model to `PriceStatus.NoPriceForDate`.
    In both cases `CostBreakdown.Total` is `null`.
  - `TokenUsage(Input, Output, CacheRead, CacheWrite)` input and a per-component `CostBreakdown`
    (`InputCost` / `OutputCost` / `CacheReadCost` / `CacheWriteCost` + nullable `Total`).
  - Model ids and aliases resolve case- and dot/dash-insensitively (`claude-opus-4.8`,
    `gpt-5-6`, dated snapshots).
- **Seed data** for the Claude 4.x/5 families (confirmed input/output rates, with Anthropic's
  documented cache multipliers: cache-read 0.1x input, 5-minute-TTL cache-write 1.25x input) and
  the OpenAI gpt-5.x families (listed as known models with no published rate yet). Numbers that
  could not be confirmed against an authoritative source are marked `Unconfirmed` or left unpriced
  rather than invented.

## [0.4.0] - 2026-07-10

### Added

- `CliThinkingLevels.Ultra` (`"ultra"`) — the top Codex reasoning rung, one step
  above `xhigh`. Codex validates this value server-side; a run that requests it now
  passes `-c model_reasoning_effort="ultra"` instead of falling back to the CLI
  config default.
- Recognition of the **gpt-5.6** Codex model family (prefix match on `gpt-5.6`,
  covering `gpt-5.6-sol`, plain `gpt-5.6`, and future variants) as supporting the
  full ladder including `xhigh` and `ultra`. A UI reading `CliCapabilities`
  (New Task dialogs, ladders) now offers those rungs for gpt-5.6 models.
- `CliThinkingLevels.DisplayName(level)` — short human labels for the rungs
  (`"Extra High"`, `"Ultra"`, …); unknown ids are echoed back rather than dropped.

### Unchanged

- Older Codex models keep their existing gating: `gpt-5` and `gpt-5-codex` top out
  at `high`, `gpt-5.5` at `xhigh` (no `ultra`).
- Claude, Gemini, and Antigravity ladders are unaffected; `ultra` is Codex-only and
  never leaks into a Claude ladder (Claude tops out at `max`).

## [0.3.1]

Baseline for this changelog.

[0.6.0]: https://github.com/agent-orc/runner/releases/tag/v0.6.0
[0.5.0]: https://github.com/agent-orc/runner/releases/tag/v0.5.0
[0.4.0]: https://github.com/agent-orc/runner/releases/tag/v0.4.0
[0.3.1]: https://github.com/agent-orc/runner/releases/tag/v0.3.1

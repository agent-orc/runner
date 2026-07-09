# Development Signals

Observations worth tracking that are **not yet decisions** — risks, rough edges,
and deprecations in flight. Each entry is checkable against the repo. This page
stays sparse on purpose: no invented signals.

## Deprecation in flight: Gemini

Gemini's public surface (`CliTypes.Gemini`, `CliRunner.Gemini`,
`CliOptions.GeminiPath`) is marked `[Obsolete]`; the driver still resolves for
existing consumers, and removal is planned before 1.0. Antigravity (`agentapi`)
is the maintained Google path. Watch: the removal will be a breaking change and
needs a version boundary.
_Source: README "Supported agents"; `CliOptions.cs` `[Obsolete]` attribute._

## Antigravity is shipped but not default-selectable

The Antigravity driver exists but is deliberately kept out of `CliTypes.All`
until a consumer migrates onto it. It currently reuses the Gemini adapter.
Watch: the day a consumer adopts it, the default set and possibly the adapter
need revisiting.
_Source: README "Supported agents" table and note._

## Pre-1.0 public API

The README states the public API "may still shift before 1.0." No API-stability
commitment exists yet. Watch: what has to settle before a 1.0 can be cut.
_Source: README status line._

## Doc-language: `docs/extraction-plan.html` is in German

English is a hard rule for this repository (public repo). The extraction-plan
document predates that rule and is written in German. It is not a blocker for the
library, but it is an open item against the English rule.
_Source: `docs/extraction-plan.html` (German body); English hard rule from the
2026-07-10 onboarding directive._

## Housekeeping: scratch files at repo root

`test_substring.csx` and `test_substring2.csx` are tracked at the repository root
and read as scratch/debug scripts (they probe `string.Contains` behaviour for
model-id matching, e.g. `"gpt-5-5"`). They look like leftovers rather than part
of the library or its test project. Worth confirming whether they should be
removed or moved.
_Source: `git ls-files`; file contents._

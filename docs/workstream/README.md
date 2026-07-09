# Workstream

The **Workstream frame** for CodingAgentRunner. This is the same five-area
structure the other Agent Studio repositories use (originally described in
agent-taskboard's `docs/concepts/engineering-workstream.md`; the concept was
renamed from "engineering workstream" to just **Workstream**). It travels with
the code so the project's living state, signals, and decisions are versioned
next to what they describe.

Everything here is in **English** — this is a public repository, and English is a
hard rule for the whole repo, wiki and docs included.

## The five areas

- **[Current Development State](current-development-state.md)** — an honest
  snapshot of where the code is right now: what is built, what is in flight, and
  what is a known gap. Updated when the state changes, not on a schedule.
- **[Development Signals](development-signals.md)** — observations worth
  tracking that are not yet decisions: risks, rough edges, deprecations in
  flight, things that will need a call later. Sparse and checkable, not a wishlist.
- **[System Knowledge](system-knowledge/)** — durable "how a part of this works
  and why" pages. Not API docs (those live in [../architecture.md](../architecture.md))
  — the reasoning and the mechanism behind a specific behaviour.
- **[Decision Log](decision-log.md)** — decisions that shaped the codebase, each
  stated as what was decided and why, sourced from the repo and its history.
- **[Workstream Log](workstream-log.md)** — a short chronological log of
  workstream-level events (onboardings, structure changes, direction changes).

## Ground rule for these pages

Honest and sparse beats filled-in. A page that says "not yet known" is more
useful than an invented one. Nothing here should assert a signal or a decision
the repository, its README, or its git history does not actually support. This
follows the same plain-statement voice as
[../voice-and-messaging.md](../voice-and-messaging.md).

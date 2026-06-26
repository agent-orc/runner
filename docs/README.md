# CodingAgentRunner — Developer Wiki

The development knowledge base for CodingAgentRunner. This wiki travels **with the
code** so the institutional memory isn't lost during the extraction.

## Pages

- **[Architecture](architecture.md)** — the modules, the public API (the `CliDescriptor` catalog, the event contract, the optional Rendering package, the Metrics namespace), and the abstractions the library leans on (logging, options, a home/path provider).
- **[Cross-CLI normalization](cross-cli-normalization.md)** — the same concept in three CLI dialects, the per-CLI frame table, the structural asymmetries the model absorbs, and the one `CliRunEvent` vocabulary the adapters fold them into.
- **[Why Windows hardening](why-windows-hardening.md)** — the war stories behind each hardening behaviour, and why each ships with a test.
- **[Process termination & abort handling](process-termination.md)** — the outcome model (`stopped` vs `completed` vs `failed`), the abort scenarios, process-tree reaping, and the watchdog.
- **[Voice & messaging](voice-and-messaging.md)** — how to write about the project: plain statements, no marketing language. Read before editing the README, the website, or any user-facing text.
- **[Extraction plan & the cut](extraction-plan.html)** — how the library is being carved out of a production orchestrator: the boundary, the migration path, and the open questions.

## Principle

Every hardening behaviour in this library was learned the hard way in a production
orchestrator. Each one links back to the incident that motivated it and ships with
a test that pins *why* it exists — so a future refactor can't quietly undo a fix
whose reason has been forgotten.

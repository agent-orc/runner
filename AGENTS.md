# Agent guide

CodingAgentRunner is a .NET library that runs coding-agent CLIs (Claude Code,
Codex, Gemini, Antigravity) as child processes. This file is intentionally short. The
detail lives in [docs/](docs/) — read it there rather than duplicating it here.

## Read these

- [docs/README.md](docs/README.md) — index of the developer wiki.
- [docs/architecture.md](docs/architecture.md) — the modules and the public API.
- [docs/why-windows-hardening.md](docs/why-windows-hardening.md) — why each
  hardening behaviour exists; each ships with a test.
- [docs/process-termination.md](docs/process-termination.md) — the outcome model
  (`completed` / `stopped` / `failed`) and the run lifecycle.
- [docs/voice-and-messaging.md](docs/voice-and-messaging.md) — how to write about
  this project: plain statements, no marketing language. Read this before editing
  the README, the website, or any user-facing text.

## Rules

- **The platform owns git.** Do not run `git commit` or `git push`. The host
  application owns version control.
- **Follow [docs/voice-and-messaging.md](docs/voice-and-messaging.md)** for any
  user-facing text.
- **Hardening ships with tests.** Each hardening behaviour links to the incident
  that motivated it and is pinned by a test. Don't remove a guard without
  understanding the reason behind it — see
  [docs/why-windows-hardening.md](docs/why-windows-hardening.md).

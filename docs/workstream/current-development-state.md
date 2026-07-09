# Current Development State

_Last updated: 2026-07-10 (initial fill during Workstream onboarding)._

An honest snapshot. Update it when the state actually changes.

## What this project is

CodingAgentRunner is an **in-process .NET library**. A .NET application
references it and uses it to launch a coding-agent CLI (Claude Code, OpenAI
Codex, Google Antigravity / `agentapi`, or the deprecated Gemini) as a **local
child process**, send it a prompt, and read its `stream-json` output as one typed
event stream — using the CLI sign-in already on the machine, with no API keys.

It is not a server, a worker node, or a remote task executor. There is no task
queue, no lease, no fencing token, and no "server URL." A caller drives runs
directly through `CliRunner` / `ICliDriver` in the same process.

> Terminology note: the onboarding card that created this frame described a
> "standalone runner MVP" with a "remote execution role" and env-driven
> `RunnerOptions`. That vocabulary describes a different kind of runner (a
> distributed worker that leases tasks from a server and ships artifacts back).
> It does not match this repository. This page and the seed knowledge pages
> describe the library that actually exists. See the
> [Decision Log](decision-log.md) note on this mismatch.

## Extraction status

The library is being **carved out of Agent Studio**, specifically the
agent-taskboard backend's `Features/Cli/` area (~59 `.cs` files). The extraction
boundary and open questions are written up in
[../extraction-plan.html](../extraction-plan.html). That document is currently in
**German** and predates the English hard rule for this repo — see
[Development Signals](development-signals.md).

Overall status, as stated on the README: **core complete, pre-1.0.** The public
API may still shift before 1.0.

## What is implemented and tested

- Hardened binary resolution (`.cmd` → `.exe`, the prompt-truncation fix) and
  the Win32 handle-scrub spawner.
- Process spawning with environment hardening, clean-context isolation, the
  platform-owns-git guard, and stdin default-deny.
- One event vocabulary (`CliRunEvent`) that each CLI's `stream-json` dialect is
  normalized onto, including the CLI's own completion signal.
- Three-valued run outcome (`completed` / `stopped` / `failed`) via
  `RunStatusClassifier`.
- Descriptor-driven CLI catalog: one `internal` engine parameterized by
  `CliDescriptor`; covering a CLI is data plus a few delegates, not a subclass.
- Quota module: per-CLI probes (`ClaudeOAuthUsageProbe`, `CodexSessionLogProbe`),
  escalation cache, cap/gate, and a machine-global cache in the OS-native
  app-data directory.
- Run metrics reconstructed from the event stream (`RunMetricsRecorder`).
- Optional `CodingAgentRunner.Rendering` package (Markdown/HTML span model).
- Environment diagnostics (`InspectEnvironment()` / `CliSetup`).

Test/CI state per the README: **427 tests, CI on Windows + Linux.**
BenchmarkDotNet micro-benchmarks exist as an optional manual run.

## Packaging

Both packages (`CodingAgentRunner`, `CodingAgentRunner.Rendering`) publish to
nuget.org from the `release` workflow on a `v*.*.*` tag, via nuget.org Trusted
Publishing (OIDC) — no stored API key. License is Apache-2.0.

## Known gaps / in flight

- **Pre-1.0 API.** The public surface may change before 1.0; consumers are told
  to pin a version.
- **Gemini deprecated.** Its public surface is `[Obsolete]`; removal is planned
  before 1.0. Antigravity is the maintained Google path.
- **Antigravity** driver ships but is kept out of the default selectable set
  (`CliTypes.All`) until a consumer migrates.
- **Doc-language and housekeeping items** are tracked in
  [Development Signals](development-signals.md).

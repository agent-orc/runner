# Architecture

> Status: planning + active extraction. **Landed** modules are real; **planned**
> modules are migrating from the source orchestrator and may change shape.

CodingAgentRunner is the *process + protocol* layer for coding-agent CLIs. It is
deliberately layered so an application can take only what it needs.

## Layers

```
┌───────────────────────────────────────────────────────────┐
│ Application (task/lane/pipeline orchestration) — OUT OF SCOPE │
└───────────────────────────────────────────────────────────┘
                 ▲ events / outcomes / quota
┌───────────────────────────────────────────────────────────┐
│ CodingAgentRunner                                          │
│   Quota       remaining-quota probe + cache       (planned)│
│   Lifecycle   stop · process-tree reap · watchdog (planned)│
│   Protocol    stream-json → events incl. completion (planned)│
│   Hardening   binary resolve(.cmd→.exe) · env · git (resolve landed) │
│   Abstractions  CliOptions · ILogger · home/log providers (landed) │
└───────────────────────────────────────────────────────────┘
                 ▲ spawns + reads
        coding-agent CLI (claude / codex / copilot / gemini)
```

## Modules

### Spawning / Hardening — *binary resolution landed*
The hard-won Windows-safe launch. **Landed:** `BinaryResolver` — resolve a `.cmd`
shim to the real `.exe` (the prompt-truncation fix), plus the `Abstractions`
(`CliOptions`, `IUserHomeProvider`, `IRunLogPathProvider`) that decouple the
library from host config. **Migrating:** environment hardening, clean-context,
the *platform-owns-git* guard.

### Protocol & completion — *planned*
Parse the CLI's `stream-json` output into a normalized `CliRunEvent` stream
(assistant text, tool calls, tool results, rate-limit frames, …) — and report
**completion from the CLI's own signal** (the `result` frame + process exit →
`TurnCompleted` / `ProcessExited`), **not** by scraping a `[[TASK_DONE]]`
sentinel. Sentinel-scraping is a fragile heuristic (it caused a false-completion
incident) and is the consumer application's own run protocol, not a library
primitive — so it stays in the app, not here.

### Lifecycle — *planned*
Stop a run, reap the whole process tree (no orphaned grandchildren holding file
handles), and a silence watchdog.

### Quota — *planned*
Per-CLI `IQuotaProbe` + a `QuotaService` that caches the remaining rate-limit
window (default TTL 10 min) with an **escalation policy**: poll more often as
usage approaches the cap (e.g. ≥90 % → every 2 min, ≥97 % → every 30 s — all
configurable). Probing spawns the CLI and is expensive, so caching is essential.

## Planned public API (sketch — not final)

```csharp
// Run a coding agent and consume a structured event stream.
var runner = new CodingAgentRunner(options, logger);
await foreach (AgentRunEvent evt in runner.RunAsync(new AgentRunOptions
{
    Cli            = AgentCli.ClaudeCode,
    Prompt         = "…",
    WorkingDirectory = repoPath,
    Model          = "…",
    CleanContext   = true,
}, cancellationToken))
{
    // evt: AssistantText | ToolCall | ToolResult | RateLimitObserved | …
}

// Remaining quota, cached + escalation-aware.
QuotaSnapshot q = await quota.GetAsync(AgentCli.ClaudeCode, ct);
```

## Abstractions the library leans on

- **Logging** — `Microsoft.Extensions.Logging.Abstractions` (`ILogger`); no concrete sink.
- **Options** — a plain options object instead of ambient `IConfiguration`.
- **Home / path provider** — so the caller decides where clean-context homes and
  caches live (no hard-coded app paths).

## Out of scope

Task lanes, pipelines, review steps, workspace/job storage — these belong in the
application built **on top** of CodingAgentRunner.

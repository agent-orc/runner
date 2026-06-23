# CodingAgentRunner

> Run coding-agent CLIs — Claude Code, Codex, Copilot, Gemini — from .NET: hardened spawning, streamed events, lifecycle & quota tracking.

[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

**CodingAgentRunner** is a .NET library for launching and supervising terminal-native coding agents (Claude Code, OpenAI Codex, GitHub Copilot CLI, Gemini CLI) as child processes — reliably, especially on Windows.

It is the "boring but critical" infrastructure layer: it spawns the agent CLI with the right binary, environment, and isolation; reads its `stream-json` output as a structured event stream; classifies the run's outcome; enforces a *platform-owns-git* boundary; and tracks remaining quota with a smart cache. Think of it as one level above [CliWrap](https://github.com/Tyrrrz/CliWrap): not "run any process", but "run a *coding agent* and understand it".

> **Status: core complete, pre-1.0.** Extracted and generalized from a production multi-agent orchestrator. The spawn engine, the four backends, the event contract, the outcome model and the quota module are all implemented and tested (146 tests, CI on Windows + Linux). The public API may still shift before 1.0 — pin a version and watch releases.

## Why

Running these CLIs from another process on Windows is full of footguns that each cost real debugging time. CodingAgentRunner encodes the fixes so you don't have to rediscover them:

- **The `.cmd`-shim prompt truncation.** On Windows `claude`/`codex` often resolve to a `.cmd` shim; spawning it routes through `cmd.exe`, which silently truncates a multi-line prompt at the first newline — so the agent receives only the first line and no task. The runner resolves and launches the real `.exe`.
- **stdin default-deny + handle scrubbing.** A Node CLI that inherits a live stdin pipe (or an unrelated parent handle) can wedge during init on Windows. Runs get an immediate-EOF stdin by default, and a Win32 spawner that hands the child only its three std pipes.
- **Platform-owns-git boundary.** A guard keeps the agent from running `git commit`/`push` (the host owns version control) — which also avoids a class of mid-run crashes.
- **Clean-context isolation.** Each run gets an isolated CLI home so concurrent runs (and your own interactive session) don't collide.
- **Completion you can trust.** The library uses the CLI's *own* completion signal (the `stream-json` result frame + process exit) — not a fragile `[[TASK_DONE]]`-scraping heuristic.
- **Honest outcomes.** A deliberate stop (user pause, watchdog) is reported as *stopped*, never as a `-1` *crash* — the distinction Windows' `Process.Kill` throws away.
- **Quota awareness.** Probe and cache the remaining rate-limit window per CLI, with an escalation policy that polls more often as you approach the limit.

See [docs/why-windows-hardening.md](docs/why-windows-hardening.md) for the full stories behind each.

## Supported agents

Claude Code · OpenAI Codex · GitHub Copilot CLI · Gemini CLI — and you can derive `CliBackendBase` to wrap your own.

## Features

- ✅ Hardened binary resolution (`.cmd`→`.exe`, the prompt-truncation fix).
- ✅ Process spawning: environment hardening, clean-context isolation, git-guard, stdin default-deny, Win32 handle-scrub spawner.
- ✅ `stream-json` → structured `CliRunEvent` stream (incl. the CLI's real completion signal) for Claude / Codex / Gemini.
- ✅ Outcome model: completed / stopped / failed, classified from exit code + stop reason.
- ✅ Durable per-stream output logs (crash-tolerant, fsync per line).
- ✅ Platform-owns-git guard (brand-neutral, configurable).
- ✅ Quota probing + escalation caching (poll harder near the limit).
- 🚧 Copilot rich streaming via PTY (currently headless-basic).
- 🚧 Concrete PTY-based quota probes (the `IQuotaProbe` contract + cache are done; plug your own probe today).

## Quickstart

```csharp
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;

// Wire the library once; resolve a backend per CLI.
var runner = new CliRunner(new CliOptions());
var backend = runner.Get("claude");

// Drive a watchdog / UI from the typed event stream.
backend.OnRunEvent += (runId, evt) =>
{
    switch (evt)
    {
        case CliRunEvent.OutputDelta d:   Console.Write(d.Text); break;
        case CliRunEvent.ToolStarted t:   Console.WriteLine($"\n[tool] {t.ToolName} {t.Argument}"); break;
        case CliRunEvent.TurnCompleted c: Console.WriteLine($"\n[done] {c.UsageSummary}"); break;
    }
};
backend.OnFinished += (runId, run) =>
    Console.WriteLine($"\nRun {runId}: {run.Status} (exit {run.ExitCode}, {run.DurationSeconds:F1}s)");

var (run, error) = await backend.StartAsync(new CliRunRequest
{
    RunId = "task-1",
    Prompt = "Add a build-status badge to the README.",
    WorkingDirectory = @"C:\repo",
    Model = "claude-opus-4-8",
    ThinkingLevel = "high",
});

// ... later, to stop a run on purpose (reported as 'stopped', not a crash):
backend.Stop("task-1");
```

### Quota with escalation caching

```csharp
using CodingAgentRunner.Quota;

// Plug your own probe (HTTP call, CLI scrape, …) behind the IQuotaProbe contract.
var quota = new QuotaService(
    probes: new[] { myClaudeQuotaProbe },
    options: new QuotaCacheOptions
    {
        DefaultTtl = TimeSpan.FromMinutes(10),
        EscalationTiers =
        [
            new QuotaEscalationTier(90, TimeSpan.FromMinutes(2)),   // ≥90% used → poll every 2 min
            new QuotaEscalationTier(97, TimeSpan.FromSeconds(30)),  // ≥97% used → every 30 s
        ],
    });

QuotaReport report = quota.GetWithBackgroundRefresh(); // cached now; refreshes stale entries in the background
```

## Project layout

```
src/CodingAgentRunner/
  CliRunner.cs                  entry point: one ICliBackend per CLI
  Abstractions/                 consumer options + IUserHome/IRunLogPath providers
  Model/                        value types, the run-outcome classifier, model catalog
  Events/                       CliRunEvent contract + phase machine
  Adapters/                     stream-json → CliRunEvent (Claude / Codex / Gemini)
  Backends/                     ClaudeBackend / CodexBackend / GeminiBackend / CopilotBackend
  Execution/                    the spawn engine, hardening, clean-context, log stores, Win32 spawner
  Quota/                        quota model, escalation cache, probe contract
tests/CodingAgentRunner.Tests/  xUnit tests
docs/                           developer wiki (architecture, the "why")
website/                        project website (static, English)
```

## Build & test

```bash
dotnet build
dotnet test
```

Requires the .NET 10 SDK.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Issues and PRs welcome.

## License

[Apache-2.0](LICENSE) © 2026 Robert Mischke.

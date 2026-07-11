# CodingAgentRunner

> Run coding-agent CLIs — Claude Code, Codex, Antigravity, Gemini — from .NET: hardened spawning, streamed events, lifecycle, quota, metrics & optional rendering.

[![NuGet](https://img.shields.io/nuget/v/CodingAgentRunner.svg?label=NuGet)](https://www.nuget.org/packages/CodingAgentRunner)
[![NuGet downloads](https://img.shields.io/nuget/dt/CodingAgentRunner.svg?label=downloads)](https://www.nuget.org/packages/CodingAgentRunner)
[![CI](https://github.com/agent-orc/runner/actions/workflows/ci.yml/badge.svg)](https://github.com/agent-orc/runner/actions/workflows/ci.yml)
[![License: Apache 2.0](https://img.shields.io/badge/License-Apache_2.0-blue.svg)](LICENSE)

```bash
dotnet add package CodingAgentRunner            # core: spawning, events, lifecycle, quota, metrics
dotnet add package CodingAgentRunner.Rendering  # optional: Markdown/HTML rendering of agent output
```

Package pages: [CodingAgentRunner](https://www.nuget.org/packages/CodingAgentRunner) · [CodingAgentRunner.Rendering](https://www.nuget.org/packages/CodingAgentRunner.Rendering) · [GitHub releases](https://github.com/agent-orc/runner/releases)

**CodingAgentRunner** gives a .NET application an LLM on your local machine, using the coding-agent CLI you already sign in to — with no API keys. Run a single prompt, or a full multi-turn session. It launches and supervises terminal-native coding agents (Claude Code, OpenAI Codex, Google Antigravity / `agentapi`, and the legacy Gemini CLI) as child processes — reliably, especially on Windows.

It is the process-and-protocol layer for those CLIs: it spawns the agent CLI with the right binary, environment, and isolation; normalizes its `stream-json` output — a different frame dialect per CLI — into one structured event vocabulary; classifies the run's outcome; enforces a *platform-owns-git* boundary; tracks remaining quota with a cache that polls more often as usage approaches the limit; records run metrics; and can render agent Markdown through an optional package. Unlike a general process wrapper such as [CliWrap](https://github.com/Tyrrrz/CliWrap), it is specialized to coding-agent CLIs — it parses their `stream-json` output and classifies the run's outcome.

> **Status: core complete, pre-1.0.** Extracted and generalized from **Agent Studio**, a production multi-agent orchestrator that has processed hundreds of millions of tokens through these CLIs. The spawn engine, descriptor-driven CLI catalog, event contract, outcome model, quota module, metrics recorder and optional rendering package are implemented and tested (427 tests, CI on Windows + Linux). BenchmarkDotNet micro-benchmarks are available as an optional manual run. The public API may still shift before 1.0 — pin a version and watch releases.

## Why

Running these CLIs from another process on Windows is full of footguns that each cost real debugging time. CodingAgentRunner encodes the fixes so you don't have to rediscover them:

- **The `.cmd`-shim prompt truncation.** On Windows `claude`/`codex` often resolve to a `.cmd` shim; spawning it routes through `cmd.exe`, which silently truncates a multi-line prompt at the first newline — so the agent receives only the first line and no task. The runner resolves and launches the real `.exe`.
- **stdin default-deny + handle scrubbing.** A Node CLI that inherits a live stdin pipe (or an unrelated parent handle) can wedge during init on Windows. Runs get an immediate-EOF stdin by default, and a Win32 spawner that hands the child only its three std pipes.
- **Platform-owns-git boundary (defence-in-depth, not a sandbox).** The *reliable* layer is a soft rule in the agent's instructions (AGENTS.md/CLAUDE.md: "don't run git; the host owns commit/push"). On top, an optional PATH-front `git` wrapper blocks mutating commands — but it is **not a hard guarantee**: a CLI that controls its own shell's PATH (e.g. claude-code's bash) can resolve the real `git` and bypass it. Treat it as an extra safety net, not a jail.
- **Clean-context isolation.** Each run gets an isolated CLI home so concurrent runs (and your own interactive session) don't collide.
- **Completion you can trust.** The library uses the CLI's *own* completion signal (the `stream-json` result frame + process exit) — not a fragile `[[TASK_DONE]]`-scraping heuristic.
- **Honest outcomes.** A deliberate stop (user pause, watchdog) is reported as *stopped*, never as a `-1` *crash* — the distinction Windows' `Process.Kill` throws away.
- **Quota awareness.** Probe and cache the remaining rate-limit window per CLI, with an escalation policy that polls more often as you approach the limit.

See [docs/why-windows-hardening.md](docs/why-windows-hardening.md) for the full stories behind each.

## Supported agents

The library targets known coding-agent CLIs in their specific versions — it is purpose-built for them, not a generic "wrap any CLI" framework, and there is no "add your own CLI" extension point.

| Agent | Type id | Status | Context | Adapter / stream | Notes |
|-------|---------|--------|---------|------------------|-------|
| Claude Code | `claude` | supported | clean or shared | Claude `stream-json` adapter | First-class driver. |
| OpenAI Codex | `codex` | supported | clean or shared | Codex `stream-json` adapter | First-class driver, including reasoning-model liveness metadata. |
| Google Gemini | `gemini` | deprecated (`[Obsolete]`) | shared only | Gemini adapter | Unsupported. Public surface marked `[Obsolete]`; removal planned before 1.0. |
| Google Antigravity (`agentapi`) | `antigravity` | driver shipped | shared only | reuses Gemini adapter | Maintained Google path; kept out of the default selectable set (`CliTypes.All`) until a consumer migrates. |
| GitHub Copilot | `copilot` | removed | n/a | no supported adapter | Removed because the headless path was PTY/TUI-dependent and did not fit the hardened structured stream engine. |

`Context` means the CLI's persistent home/state, not the repository or prompt size.
`clean` creates a temporary per-run CLI home and seeds only the minimum auth/base config
(Claude via `CLAUDE_CONFIG_DIR`, Codex via `CODEX_HOME`). `shared` uses the operator's
normal signed-in CLI home, including settings, cache, memory and prior CLI state. Repo
files and repo instruction files such as `AGENTS.md` / `CLAUDE.md` remain visible in
both modes because they come from the checkout, not from the CLI home.

Internally each CLI is a `CliDescriptor` — data plus a few pure delegates — in a fixed catalog, and one `internal sealed` engine is parameterized by it; covering a CLI is a descriptor in the library, not a subclass and not a consumer plug-in. The adapters fold each CLI's own dialect onto one event vocabulary, so your run logic is written once. See [Architecture](docs/architecture.md) and [Cross-CLI normalization](docs/cross-cli-normalization.md).

> **Google status:** Gemini is deprecated, unsupported, and superseded by Antigravity, Google's `agentapi` CLI. Its public surface (`CliTypes.Gemini`, `CliRunner.Gemini`, `CliOptions.GeminiPath`) is marked `[Obsolete]`; the driver still resolves for existing consumers, and removal is planned before 1.0. Antigravity is where Google integration work belongs.

## Features

- ✅ Hardened binary resolution (`.cmd`→`.exe`, the prompt-truncation fix).
- ✅ Process spawning: environment hardening, clean-context isolation, git-guard, stdin default-deny, Win32 handle-scrub spawner.
- ✅ One event vocabulary across CLIs: each CLI's `stream-json` dialect normalized to the same closed `CliRunEvent` sum type (incl. the CLI's real completion signal) — write run logic once, never branch on the CLI or model.
- ✅ One terminal event: `RunEnded` with a 3-valued outcome (completed / stopped / failed), classified from exit code + stop reason.
- ✅ Typed interrupt classification (opt-in): an `IInterruptClassifier` raises a `CliRunEvent.Interrupt(InterruptReason, …, IsFatal)` for stop-worthy conditions (environment blocker, quota exhausted, silent completion, …); the library emits the event, your code decides whether to `Stop`.
- ✅ Per-CLI capabilities (`CliCapabilities`: clean-context, resume, heartbeat-during-thinking, thinking levels) and overridable defaults resolved by specificity (`CliScope` / `CliDefault<T>`: CLI ▸ model ▸ thinking level) — ask the capability, don't switch on the CLI type.
- ✅ Built-in silence watchdog (`RunWatchdog` / `WatchdogPolicy`) — one-line attach, phase-aware budgets.
- ✅ Durable per-stream output logs (crash-tolerant, fsync per line).
- ✅ Platform-owns-git guard (brand-neutral, configurable).
- ✅ Environment diagnostics (`CliRunner.InspectEnvironment()`): which CLIs are installed and signed in, plus the install command and sign-in steps for anything missing — as data (`EnvironmentReport` / `CliSetup`) and as a renderable text report.
- ✅ Quota cache · escalation · cap/gate · free event-harvest (poll harder near the limit; skip a run before it hits the wall) — with built-in probes for Claude (OAuth usage endpoint, real server-side percent) and Codex (session-log rate limits).
- ✅ Pluggable process spawner (`CliOptions.Spawner` / `ICliProcessSpawner`) — inject a custom launcher (e.g. a Windows PTY); null uses redirected pipes.
- ✅ Run metrics from the event stream (`RunMetricsRecorder`) plus an optional `CodingAgentRunner.Rendering` package for Markdown/HTML UI output.
- ✅ Optional BenchmarkDotNet micro-benchmarks for adapter parsing, usage parsing and rendering hot paths.
- ✅ Built-in quota probes: `ClaudeOAuthUsageProbe` (the CLI's own usage endpoint) and `CodexSessionLogProbe` (rollout-log rate limits). The `IQuotaProbe` seam stays open for your own.

## Quickstart

```csharp
using CodingAgentRunner;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;

// Wire the library once; resolve a driver per CLI.
var runner = new CliRunner(new CliOptions());
var driver = runner.Get("claude");

// Drive a watchdog / UI from the typed event stream.
driver.OnRunEvent += (runId, evt) =>
{
    switch (evt)
    {
        case CliRunEvent.OutputDelta d:   Console.Write(d.Text); break;
        case CliRunEvent.ToolStarted t:   Console.WriteLine($"\n[tool] {t.ToolName} {t.Argument}"); break;
        case CliRunEvent.TurnCompleted c: Console.WriteLine($"\n[done] {c.UsageSummary}"); break;
    }
};
driver.OnFinished += (runId, run) =>
    Console.WriteLine($"\nRun {runId}: {run.Status} (exit {run.ExitCode}, {run.DurationSeconds:F1}s)");

var (run, error) = await driver.StartAsync(new CliRunRequest
{
    RunId = "task-1",
    Prompt = "Add a build-status badge to the README.",
    WorkingDirectory = @"C:\repo",
    Model = "claude-opus-4-8",
    ThinkingLevel = "high",
});

// ... later, to stop a run on purpose (reported as 'stopped', not a crash):
driver.Stop("task-1");
```

### Prerequisites: the CLI must be installed and signed in

The library runs CLIs you already have — it does not install or authenticate
them. `InspectEnvironment()` reports what is missing and how to fix it:

```csharp
using CodingAgentRunner.Diagnostics;

var report = new CliRunner().InspectEnvironment();
if (!report.AnyReady)
{
    Console.WriteLine(report.ToText());
    // claude      NOT INSTALLED (probed 'claude')
    //             install: npm install -g @anthropic-ai/claude-code
    //             NOT SIGNED IN — Run `claude` in a terminal; the first run opens a browser sign-in ...
    //             docs: https://code.claude.com/docs/en/setup
    // ...
}

// Programmatic, per CLI:
CliEnvironmentStatus codex = report.For("codex")!;
if (!codex.Installed) Console.WriteLine(codex.Setup.RecommendedInstallCommand);
```

The static setup knowledge (install commands, sign-in steps, headless/CI auth
options, credential-file locations) is also available without probing anything,
via `CliSetup.For("claude")`. See [docs/cli-setup.md](docs/cli-setup.md) for the
per-CLI install and sign-in guide, including the non-interactive options.

### Quota with escalation caching

```csharp
using CodingAgentRunner.Quota;

// Built-in probes: Claude (the CLI's own OAuth usage endpoint — real server-side
// percent) and Codex (freshest rate_limits entry from the CLI's session logs).
// The IQuotaProbe seam stays open for your own probes.
var quota = new QuotaService(
    probes: [new ClaudeOAuthUsageProbe(), new CodexSessionLogProbe()],
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

// Shared per-user cache: every process that opts in shares one cache file in
// the OS-native app-data dir (%LOCALAPPDATA%\coding-agent-runner on Windows,
// ~/Library/Application Support on macOS, ~/.local/share on Linux; override
// with CODING_AGENT_RUNNER_CACHE_DIR). A snapshot probed by one application
// is adopted by the others instead of re-probing; concurrent writers merge
// per CLI (freshest wins).
var shared = new QuotaService(
    probes: [new ClaudeOAuthUsageProbe(), new CodexSessionLogProbe()],
    store: FileQuotaCacheStore.Global());

// Free live updates: harvest rate-limit events from runs you execute anyway.
driver.OnRunEvent += (_, evt) => quota.Observe(driver.CliType, evt);

// Skip a run before it hits the wall.
quota.Cap("claude", stopAtPercent: 95);
if (!quota.Gate("claude").Allowed) { /* defer the run */ }
```

Quota-limit waiting is a separate opt-in policy. When a fresh probe reports a
future reset within the threshold, the runner emits `QuotaWaitStarted`, stops the
exhausted process, waits asynchronously, emits `QuotaWaitEnded`, and restarts the
same request. Unknown quota and resets outside the threshold keep the existing
failure or fallback route.

```csharp
var runner = new CliRunner(new CliOptions
{
    WaitOnQuota = new WaitOnQuotaOptions
    {
        Enabled = true,
        Threshold = TimeSpan.FromMinutes(30), // default
        QuotaService = quota,
    },
});
```

### Run metrics from events

```csharp
using CodingAgentRunner.Metrics;

var metrics = new RunMetricsRecorder();
driver.OnRunEvent += (_, evt) => metrics.Observe(evt);

driver.OnFinished += (runId, _) =>
{
    RunMetrics snapshot = metrics.Build();
    Console.WriteLine($"first output: {snapshot.TimeToFirstOutputMs / 1000.0:F1}s");
    Console.WriteLine($"output tokens/sec: {snapshot.AverageOutputTokensPerSec:F1}");
};
```

Metrics are reconstructed from the same `CliRunEvent` stream your UI or watchdog
already consumes. The recorder does not poll the process or parse raw logs.

### Optional rendering package

```csharp
using CodingAgentRunner.Rendering;

IReadOnlyList<RenderedLine> lines = MarkdownRenderer.ToLines(agentMarkdown);
string html = string.Concat(lines.Select(HtmlRenderer.SpansToHtml));
```

`CodingAgentRunner.Rendering` is opt-in and one-way (`Rendering` depends on core;
core never references `Rendering`). It maps agent Markdown onto a presentation-neutral
span/line model, injects links through a pluggable `LinkResolver`, and can materialize
Markdown or HTML for UI consumers. The default resolver (`LinkExtractor.WebDefault`)
enforces an http/https/mailto allowlist that rejects `javascript:` / `data:` targets,
so HTML output is XSS-safe by default. Event-stream consumers never pay this dependency.

## Project layout

```
src/CodingAgentRunner/
  CliRunner.cs                  entry point: resolves one ICliDriver per CLI from the catalog
  Abstractions/                 consumer options + IUserHome/IRunLogPath providers
  Model/                        value types, the run-outcome classifier, model catalog, CliCapabilities
  Events/                       CliRunEvent contract, phase machine, Interrupt + InterruptReason, watchdog
  Adapters/                     stream-json → CliRunEvent (Claude / Codex / Gemini; Antigravity reuses Gemini)
  Diagnostics/                  InspectEnvironment report + per-CLI setup knowledge (CliSetup)
  Execution/                    CliRunEngine (one engine parameterized by CliDescriptor), CliCatalog,
                                BuiltInDescriptors, LaunchSpec, hardening, clean-context, log stores, Win32 spawner
  Metrics/                      RunMetrics / TurnMetrics / RunMetricsRecorder / UsageSummaryParser
  Quota/                        quota model, escalation cache, probe contract
src/CodingAgentRunner.Rendering/  optional, opt-in: span/line model, link injection,
                                Markdown/HTML rendering (depends on core; core does not depend on it)
tests/CodingAgentRunner.Tests/  xUnit tests
benchmarks/CodingAgentRunner.Benchmarks/  BenchmarkDotNet micro-bench: parse / metrics / render throughput
docs/                           developer wiki (architecture, the "why")
website/                        project website (static, English)
website/data/cli-performance-observations.json
                                measured end-to-end CLI performance scenario data with source-test references
```

## Build & test

```bash
dotnet build
dotnet test
```

Requires the .NET 10 SDK.

### Optional benchmarks

Micro-benchmarks of the library's own parsing, metrics and rendering overhead — the
per-line cost the host pays while reading agent output (not end-to-end *model*
benchmarks, which would mean spawning a real CLI). These are optional manual runs;
`dotnet test` and release validation do not execute BenchmarkDotNet:

```bash
dotnet run -c Release --project benchmarks/CodingAgentRunner.Benchmarks -- --filter '*'
dotnet run -c Release --project benchmarks/CodingAgentRunner.Benchmarks -- --filter '*' --job dry
```

See [benchmarks/CodingAgentRunner.Benchmarks](benchmarks/CodingAgentRunner.Benchmarks)
for the benchmark classes and the fast smoke command.

End-to-end CLI performance observations live separately in
[website/data/cli-performance-observations.json](website/data/cli-performance-observations.json).
Those rows are measured local CLI executions, and each scenario links to source-level
tests through `sourceTests`. The JSON also records `contextTokens`,
cached/cache-creation input tokens, output tokens, reasoning tokens and
`totalTokensUsed` so first-output latency can be compared against real prompt size.

## Releasing

Both packages (`CodingAgentRunner` and `CodingAgentRunner.Rendering`) are
published to **nuget.org** by the `release` GitHub workflow when a `v*.*.*` tag
is pushed; the package version is derived from the tag. The workflow also
creates a **GitHub Release** for the tag, with generated notes, links to the
nuget.org package pages, and the `.nupkg` files attached.

```bash
scripts/release.sh 0.1.0      # validates, tests, tags v0.1.0, pushes the tag
# scripts/pack.sh             # local pack into ./artifacts (no publish)
```

Auth uses nuget.org **Trusted Publishing** (OIDC) — there is **no API key** to
create, store, or rotate. GitHub Actions mints a short-lived OIDC token that
nuget.org validates against a Trusted Publishing policy (package owner + this
repo + `release.yml`). Nothing secret lives in the repo, the scripts, or the
workflow. While the API is pre-1.0, publish `0.x` versions.

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md). Issues and PRs welcome.

## License

[Apache-2.0](LICENSE) © 2026 Robert Mischke.

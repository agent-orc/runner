# Architecture

CodingAgentRunner is the *process + protocol* layer for coding-agent CLIs: it
spawns a CLI as a child process, reads its `stream-json` output as a typed event
stream, classifies the run's outcome, and tracks quota. Everything above that —
task lanes, pipelines, review steps, storage — belongs in the application built
on top.

## Layers

```
┌────────────────────────────────────────────────────────────────┐
│ Application (task / lane / pipeline orchestration) — out of scope │
└────────────────────────────────────────────────────────────────┘
                 ▲ events · outcomes · quota
┌────────────────────────────────────────────────────────────────┐
│ CodingAgentRunner                                                │
│   Quota       remaining-quota cache · escalation · cap/gate       │
│   Lifecycle   stop · process-tree reap · built-in watchdog        │
│   Protocol    stream-json → typed CliRunEvent (incl. completion)  │
│   Hardening   binary resolve (.cmd→.exe) · env · stdin · git      │
│   Abstractions  CliOptions · ILogger · home/log providers          │
└────────────────────────────────────────────────────────────────┘
                 ▲ spawns + reads
        coding-agent CLI (claude · codex · gemini · antigravity)
```

## The public surface

The entry point is `CliRunner`. From a single `CliOptions` it builds one `ICliDriver`
per supported CLI — but a driver is **not** a per-CLI subclass. Each is one
parameterized engine, `CliRunEngine`, constructed from a `CliDescriptor` resolved
through an `ICliCatalog`. Adding a CLI means registering another descriptor in the
catalog, not writing a new class. `runner.Get("claude")` (or the `runner.Claude`
sugar) returns that descriptor-backed engine as an `ICliDriver`.

```csharp
var runner = new CliRunner(new CliOptions());
var driver = runner.Get("claude");                 // or runner.Claude

driver.OnRunEvent += (runId, evt) => { /* watchdog / UI */ };

var (run, error) = await driver.StartAsync(new CliRunRequest
{
    RunId            = "task-1",
    Prompt           = "Refactor the parser.",
    WorkingDirectory = repo,
});
```

Most uses need five public types — `CliRunner`, `CliOptions`, `CliRunRequest`,
`CliRunInfo`, and `CliRunEvent`. One layer down the descriptor model is also public
and inspectable: `CliDescriptor`, `ICliCatalog` / `CliCatalog`, and the launch-time
`LaunchSpec` / `CliLaunchContext`. The per-CLI stream-json adapters are public too —
`ClaudeEventAdapter` / `CodexEventAdapter` / `GeminiEventAdapter` each expose a static
`Map(line, runId)`, so you can turn a CLI's output line into typed events without
spawning a process (replay, tests, benchmarks). The built-in descriptors and the run
engine (`CliRunEngine`) stay `internal` — reachable only through `CliRunner` / the
catalog. The spawner, the hardening, and the log stores stay `internal` too: that
machine room is not part of the contract, and there is **no "add your own CLI"
extension point** today — the descriptors are a fixed, inspectable catalog, not a
registration hook. The one launch-time seam is `CliOptions.Spawner` (see below).

**Use, don't extend.** You consume the library through interfaces and records; you do
not subclass it. A CLI is *data* — a `CliDescriptor` is a sealed record of fields plus
a few pure delegates (`BuildLaunch`, `Parse`, `InterruptClassifier`, `Capabilities`,
optional `CleanContext` / liveness), not a base class with `protected virtual` seams.
There is exactly one run engine, `internal sealed`, parameterized by the descriptor;
a consumer cannot subclass it or even name it. That is the design's load-bearing
invariant: a new CLI is one descriptor's worth of data, and nothing about the engine
changes.

## Modules

### Spawning / hardening
Windows-safe launch: `BinaryResolver` resolves a `.cmd` shim to the real `.exe`
(the prompt-truncation fix); environment hardening; stdin default-deny; a
handle-scrubbing Win32 spawner that hands the child only its three std pipes. The
spawner is pluggable through `CliOptions.Spawner` (`ICliProcessSpawner`) — inject a
custom launcher, e.g. a Windows pseudo-terminal, or leave it null for redirected
pipes. The *platform-owns-git* guard injects a PATH-front `git` wrapper that blocks
mutating commands; it is defence-in-depth, not a sandbox.

### Protocol & completion
Each per-CLI adapter maps the CLI's `stream-json` frames onto the normalized
`CliRunEvent` contract — a closed sum type, so a `switch` over it is checked for
exhaustiveness by the compiler. Completion comes from the CLI's own `result` frame
plus process exit (surfaced as `TurnCompleted` and the single terminal `RunEnded`),
**not** from scraping a `[[TASK_DONE]]` sentinel, which is a fragile heuristic and
stays in the consumer's run protocol, not here.

### Cross-CLI normalization
The three CLIs speak the same ideas in different dialects: a session start is a
`system` frame in Claude, a `thread.started` in Codex, an `init` in Antigravity; the
id it carries is `session_id` in two and `thread_id` in the third; the cached-token
field has three different names. The adapters fold all of that onto one event
vocabulary, so a consumer's run logic is written once and never branches on the CLI
(or the model). Parsing is by frame *type*, never by model — the model is metadata, so
a new model needs no new parser branch. The per-CLI dialect table, the structural
asymmetries the model absorbs, and the two shipped projections (the event stream and
the optional render line/span model) are in
[Cross-CLI normalization](cross-cli-normalization.md).

### Interrupt classification
Beyond the terminal `RunEnded`, a run can surface a typed
`CliRunEvent.Interrupt(InterruptReason Reason, string Detail, bool IsFatal)` when a
classifier recognizes a stop-worthy condition in the output. `InterruptReason` is a
closed enum: `EnvironmentBlocker` (an OS/sandbox error the agent can't self-recover
from — continuing only burns the silence budget against the same wall),
`QuotaExhausted` (recoverable after the window resets), `Sentinel` (a consumer-defined
completion marker), `SelfReference` (the scanner self-reference trap — raised with
`IsFatal: false` so it is not mistaken for a real blocker), `NeedsInput` (blocking on
input that won't arrive unattended), and `SilentCompletion` (a legacy CLI that stopped
without a terminal completion frame). Classifiers implement `IInterruptClassifier`;
the library raises the event, and the consumer keeps `Stop()` authority — deciding
based on `IsFatal`. The mechanism and a starter `EnvironmentBlocker` classifier ship;
the built-in descriptors leave the classifier unset by default, so a consumer opts in
(or supplies its own grammar) rather than getting surprise stops.

### Capabilities
A descriptor answers "what can this CLI do?" through `CliCapabilities`, resolved per
model via `ICliDriver.Capabilities(model)`: `SupportsCleanContext`, `SupportsResume`,
`SupportsThinking` with the `ThinkingLevels` / `DefaultThinkingLevel` it accepts,
`EmitsHeartbeatDuringThinking` (true for Codex's reasoning models, which ping while
they think, and false for the others), and an open `Knobs` dictionary for the rest.
The point is that a consumer **asks the capability, not the CLI type** — so logic
written against `EmitsHeartbeatDuringThinking` (to widen a silence budget, say) covers
a new CLI the moment its descriptor is registered, with no `if (cliType == …)` branch.

### Defaults — batteries-included, overridable
The "normal knowledge" lives in the library, not the consumer. Thinking levels
normalize against the model (`CliThinkingLevels`) and reasoning flags are per CLI and
model (`CliReasoningFlags`), so the sensible value is the default and the consumer
overrides only its delta. For layering your own defaults the same way, `CliScope`
(`CliType`, optional `Model`, optional `ThinkingLevel`) plus `CliDefault<T>` resolve a
value most-specific-first — CLI + Model + ThinkingLevel, then CLI + Model, then
CLI + ThinkingLevel, then CLI, then a global fallback — and `Set(scope, value)` is the
one-line override. (The resolution primitive ships; the library does not yet pre-seed
a registry of `CliDefault<T>` values beyond the thinking/reasoning tables above.)

### Lifecycle
Stop a run — reported as `RunEnded(Stopped, …)`, a deliberate stop, never a crash —
reap the whole process tree (no orphaned grandchildren holding file handles), and a
built-in, phase-aware silence watchdog (`RunWatchdog` / `WatchdogPolicy`) you attach
in one line.

### Quota
A per-CLI `IQuotaProbe` contract plus a `QuotaService` that caches the remaining
window with an escalation policy (default TTL 10 min; poll harder near the limit,
e.g. ≥90 % → every 2 min, ≥97 % → every 30 s — all configurable), a cap/gate to skip
a run before it hits the wall, and a free event-harvest (`Observe`) that keeps the
cache warm from `RateLimitObserved` events. You supply the probe; the library does
the caching, escalation, persistence, and cap check around it.

### Run metrics
A small `CodingAgentRunner.Metrics` namespace (in the core package) folds the event
stream into a structured summary: `TurnMetrics` and `RunMetrics` records accumulated
by `RunMetricsRecorder`, with token figures parsed by `UsageSummaryParser` from each
`TurnCompleted` usage line. Time-to-first-output, time-to-session-id, per-turn
wall-clock and output-tokens/sec are reconstructed from the events' timestamps — it
records, it does not poll.

### Optional: Rendering (separate package)
`CodingAgentRunner.Rendering` is a separate, opt-in NuGet package — the core library
has **no** dependency on it; the dependency points one way, Rendering → core. It maps
agent output onto a presentation-agnostic span/line model, injects links through a
pluggable `LinkResolver` (so task-refs, file links and web URLs are the consumer's
policy, not the library's), and materializes Markdown or HTML (Markdig-backed). A
consumer that only needs the event stream never references it.

## Supported CLIs

| CLI | Type id | Status | Context | Adapter / stream | Notes |
|-----|---------|--------|---------|------------------|-------|
| Claude Code | `claude` | supported | clean or shared | Claude adapter | First-class driver. |
| OpenAI Codex | `codex` | supported | clean or shared | Codex adapter | First-class driver, including reasoning-model liveness metadata. |
| Google Gemini | `gemini` | deprecated | shared only | Gemini adapter | Legacy Google driver kept for compatibility; no new feature work. |
| Google Antigravity (`agentapi`) | `antigravity` | driver shipped | shared only | reuses Gemini adapter | Maintained Google path; kept out of `CliTypes.All` until a consumer migrates. |

The GitHub Copilot driver was supported earlier but has been removed: its headless
surface was PTY/TUI-dependent and couldn't share the hardened structured spawn/stream
engine cleanly.

## Abstractions the library leans on

- **Logging** — `Microsoft.Extensions.Logging.Abstractions` (`ILogger`); no concrete sink.
- **Options** — a plain `CliOptions` object instead of ambient `IConfiguration`.
- **Home / path providers** — the caller decides where clean-context homes and logs
  live (`IUserHomeProvider`, `IRunLogPathProvider`); no hard-coded app paths.

## Out of scope

Task lanes, pipelines, review steps, workspace/job storage — these belong in the
application built **on top** of CodingAgentRunner.

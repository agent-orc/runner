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
and inspectable: `CliDescriptor`, `ICliCatalog` / `CliCatalog`, `BuiltInDescriptors`,
and the launch-time `LaunchSpec` / `CliLaunchContext`. The per-CLI adapters, the
spawner, the hardening, and the log stores stay `internal`: the machine room is not
part of the contract, and there is **no "add your own CLI" extension point** today —
the descriptors are a fixed, inspectable catalog, not a registration hook. The one
launch-time seam is `CliOptions.Spawner` (see below).

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
based on `IsFatal`.

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

| CLI | Type id | Notes |
|-----|---------|-------|
| Claude Code | `claude` | clean-context capable |
| OpenAI Codex | `codex` | clean-context capable |
| Google Antigravity (`agentapi`) | `antigravity` | the maintained Google integration; shared-only; reuses the Gemini event adapter |
| Google Gemini | `gemini` | **deprecated**, superseded by Antigravity; shared-only |

The GitHub Copilot driver was supported earlier but has been removed: its headless
surface couldn't share the hardened spawn/stream engine cleanly.

## Abstractions the library leans on

- **Logging** — `Microsoft.Extensions.Logging.Abstractions` (`ILogger`); no concrete sink.
- **Options** — a plain `CliOptions` object instead of ambient `IConfiguration`.
- **Home / path providers** — the caller decides where clean-context homes and logs
  live (`IUserHomeProvider`, `IRunLogPathProvider`); no hard-coded app paths.

## Out of scope

Task lanes, pipelines, review steps, workspace/job storage — these belong in the
application built **on top** of CodingAgentRunner.

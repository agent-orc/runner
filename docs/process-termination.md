# Process termination & abort handling

> Status: design + active migration. The classification model is the contract;
> the lifecycle pieces (reaping, watchdog) are migrating from the source.

When you run a coding-agent CLI unattended, the child process ends in many ways —
you stop it, you cancel it, it finishes, it self-crashes, something external kills
it, or it leaves orphaned grandchildren behind. CodingAgentRunner's job is to turn
all of those into a **clean, distinguishable outcome** for the consumer.

## The outcome model: did *I* stop it, or did it *die*?

The load-bearing distinction is whether a termination was **runner-initiated** or
not. Every stop the runner performs records a `RunStopReason`; a process that ends
without one was not stopped by us.

`RunStatusClassifier.Classify(exitCode, stopReason)`:

| Condition | Status | Meaning |
|---|---|---|
| `stopReason != None` | **stopped** | The runner ended it on purpose (user stop, cancel, watchdog, quota, …). |
| `stopReason == None` and `exitCode == 0` | **completed** | The CLI finished on its own, cleanly. |
| `stopReason == None` and `exitCode != 0` | **failed** | The CLI died on its own (crash / non-zero exit). |

This is what lets a consumer tell *"I asked it to stop"* apart from *"it crashed"* —
without that, every kill looks like a failure. On Windows, a .NET `Process.Kill`
hands back exit code `-1`, so `failed` + `exit=-1` + `stopReason=None` is the
signature of a self-crash, **not** of a runner kill (a runner kill always carries a
reason).

## `RunStopReason`

```
None, UserStop, FollowupPause, Watchdog, Cancelled,
QuotaCapExceeded, QuotaResetWait, SentinelDetected, EnvironmentBlocker, SilentCompletion
```

The consumer supplies the reason when it calls `Stop`; the runner supplies the rest
(watchdog, cancellation).

### `Interrupt` is the signal; `Stop` is the action

`RunStopReason` records *why a run was terminated*. A separate, non-terminal channel
records *that the library noticed a stop-worthy condition in the output*: the engine
runs each descriptor's `IInterruptClassifier` per line and, on a match, emits a typed
[`CliRunEvent.Interrupt(InterruptReason, Detail, IsFatal)`](cross-cli-normalization.md)
into the same event stream. The library only raises the event — it never stops the run
itself. The consumer reads it and decides: typically, on an `IsFatal` interrupt, call
`Stop(reason)` with the matching `RunStopReason`. The two enums overlap by design —
`EnvironmentBlocker` and `SilentCompletion` appear in both `InterruptReason` (the
observation) and `RunStopReason` (the resulting deliberate stop) — so a fatal
`EnvironmentBlocker` interrupt becomes a `Stop(EnvironmentBlocker)`, reported as
`stopped`, never as a `-1` crash. See [Architecture › Interrupt classification](architecture.md).

## Abort scenarios the runner is built to handle

| Scenario | Handling |
|---|---|
| **User stop** | `Stop(runId, UserStop)` → kill the process tree → `stopped`. |
| **Cancellation** | The `CancellationToken` fires → tree kill → `stopped`/`Cancelled`. |
| **Silence / hang** | A watchdog tracks the last-streamed clock; past the *hung* threshold it stops the run (`Watchdog`). |
| **Clean finish** | The CLI emits its completion signal and exits `0` → `completed` (no scraping; see [why-windows-hardening](why-windows-hardening.md)). |
| **Self-crash** | The CLI exits non-zero with no stop reason → `failed`; nothing is left running. |
| **Orphaned grandchildren** | A run can spawn its own children (a dev server, a build worker). Stop reaps the **whole tree**; a startup + periodic sweep reaps orphans a previous run left behind (they otherwise hold working-directory handles and wedge the next run). |

## Process-tree reaping

Killing only the direct child leaves grandchildren (e.g. a Node dev server) alive,
holding file handles in the working directory. The runner kills the **entire
process tree**, and a periodic sweep reaps orphans from prior runs whose tracking
was lost — guarded by a PID-recycling check (process name + start time) so it never
kills an unrelated process that inherited a recycled PID.

## Watchdog

A silence-based liveness watch with configurable thresholds (quiet → suspicious →
hung). Because it acts on *silence*, it only fires when a run has genuinely gone
quiet — an actively streaming run is never killed, even a long one.

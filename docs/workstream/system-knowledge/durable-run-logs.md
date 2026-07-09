# Durable run logs

> Honest counterpart of the onboarding card's "artifact/log shipping" topic.
> Nothing is shipped to a server. The library writes **durable, local, per-stream
> output logs** for each run and merges them on read. This page describes that
> mechanism.

## Layout: one append-only file and one lock per stream

`src/CodingAgentRunner/Execution/Logging/RunLogStore.cs`. Each run gets a
directory; each logical stream of that run (`stdout`, `stderr`, and any
synthetic/system stream) gets its own append-only file and its own lock:

```
<runDir>/<stream>.jsonl
```

The streams are never written to a shared file. The reason is concrete even for a
single CLI process: stdout and stderr are pumped by two **separate reader
threads** that both append at once. With one shared file they would serialise on
a single lock, and an interrupted writer could leave the shared handle open — a
"file in use" failure on Windows. One writer owning one file removes that
contention by construction, and an orphaned writer can only ever affect its own
stream, never poison the whole run.

## Crash-tolerance

The per-stream store (`CliOutputLogStore`) appends line by line. The README
describes these as "durable per-stream output logs (crash-tolerant, fsync per
line)": a crash loses at most the line in flight, not the log.

## Merge on read, not on write

The interleaved, timestamp-ordered view is computed **on read**
(`RunLogStore.ReadMerged`), not by everyone writing one file. `ReadMerged` is
`static`, so it works after the owning store is disposed — after the run
finished, the host restarted, or a reviewer opens the logs later. It also falls
back to a legacy single-file layout (`<runDir>.jsonl`) when present. `OrderBy` is
a stable sort in .NET, so lines sharing a millisecond keep their per-file append
order.

## Lifecycle helpers

- `Reset()` clears a run directory so a re-attempt does not accumulate stale
  lines (it drops open writers first, because Windows refuses to delete a file
  with an open handle).
- `DeleteRun(runDir)` removes a run's directory and any legacy single file
  (best-effort).
- `LastAppendError` surfaces the reason of the most recent failed append.

## What this is not

These logs are the run's own local record. There is no upload, no remote sink,
and no "log shipping" pipeline in this library — a host that wants logs elsewhere
reads them (via `ReadMerged`) and forwards them itself.

_Sources: `Execution/Logging/RunLogStore.cs`; `Execution/Logging/CliOutputLogStore.cs`;
README "Features" (durable per-stream output logs)._

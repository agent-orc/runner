# CodingAgentRunner.Benchmarks

[BenchmarkDotNet](https://benchmarkdotnet.org) micro-benchmarks of the library's own
hot paths — the work the host does **per line of agent output** while a run streams:

- **`AdapterParsingBenchmarks`** — `stream-json` line → typed `CliRunEvent`s, for each
  of Claude / Codex / Gemini, over a representative transcript (session start, text,
  tool call, tool result, terminal completion). This is the hottest path; it runs once
  per output line for the whole length of a run.
- **`UsageParsingBenchmarks`** — `UsageSummaryParser.Parse`, the per-turn usage line →
  token figures the metrics recorder folds in (once per `TurnCompleted`).
- **`RenderingBenchmarks`** — the optional `CodingAgentRunner.Rendering` package:
  Markdown → the span/line model, and a line → HTML (once per rendered message in a UI
  consumer; the core event-stream consumer never pays this).

These measure the **library's** overhead and allocation profile, not the agents. They
are deliberately *not* end-to-end model benchmarks — comparing models or wall-clock per
prompt means actually spawning a CLI and burning tokens, which belongs in a separate
harness, not a CI-friendly micro-bench.

## Run

```bash
# all benchmarks (full statistical run — minutes)
dotnet run -c Release --project benchmarks/CodingAgentRunner.Benchmarks -- --filter '*'

# one class
dotnet run -c Release --project benchmarks/CodingAgentRunner.Benchmarks -- --filter '*AdapterParsing*'

# fast smoke check (single invocation, no statistics)
dotnet run -c Release --project benchmarks/CodingAgentRunner.Benchmarks -- --filter '*' --job dry
```

Release config is mandatory — BenchmarkDotNet refuses to run a Debug build. Results
(reports, logs) land in `BenchmarkDotNet.Artifacts/`, which is git-ignored.

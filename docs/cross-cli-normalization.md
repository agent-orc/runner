# Cross-CLI normalization — one vocabulary, three dialects

Claude Code, OpenAI Codex, and Google Antigravity (and the deprecated Gemini CLI)
all stream **NDJSON on stdout** — one JSON object per line — but each uses its own
frame vocabulary for the same ideas. A "session started" is a `system` frame in one,
a `thread.started` in another, an `init` in a third; the id that comes with it is
called `session_id` here and `thread_id` there.

CodingAgentRunner's per-CLI adapters map every line onto **one** normalized event
type, [`CliRunEvent`](../src/CodingAgentRunner/Events/CliRunEvent.cs). A consumer
writes its run logic once, against that one vocabulary, and never branches on which
CLI — or which model — produced a line.

## Same concept, three dialects

Each row is one concept. The cells are the actual frame each CLI puts on the wire;
an empty cell means that CLI has no such concept. The right column is the single
`CliRunEvent` the adapters produce.

| Concept | Claude Code | OpenAI Codex | Antigravity / Gemini | Normalized to |
|---|---|---|---|---|
| Session start | `system` init frame, `session_id` | `thread.started`, `thread_id` | `init`, `session_id` | `SessionStarted` |
| Assistant text | `assistant` content `text` block | `item` `agent_message` | `message` (`content` string) | `OutputDelta` |
| Thinking / reasoning | `thinking` block (dropped from the typed stream) | `reasoning` item | — | `Heartbeat` (Codex); Claude thinking emits no typed event |
| Tool call | `tool_use` `{ name, input }` | `item` `command_call` | `tool_call` `{ name, parameters }` (snake_case: `run_shell_command`, `read_file`) | `ToolStarted` |
| Tool result | `tool_result` content | the `command_call` item's `item.completed` | `tool_result` | `ToolCompleted` |
| Plan / TODO update | `tool_use` `TodoWrite` | `item` `update_plan` | — | `PlanUpdated` |
| Turn complete | `result`, `is_error=false` | `turn.completed` | `result`, `status=success` | `TurnCompleted` |
| Turn error | `result`, `is_error=true` | `turn.failed`, `error.message` | `result`, `status!=success` | `TurnFailed` |
| Token usage | `usage` `{ input_tokens, output_tokens, cache_read_input_tokens }` | `usage` `{ input_tokens, cached_input_tokens, output_tokens, reasoning_output_tokens }` | `stats` `{ input_tokens, output_tokens, cached }` | usage summary on `TurnCompleted` |
| Rate limit | `rate_limit_event` (status + reset, no percent) | `token_count` `rate_limits` (core protocol / rollout logs, NOT the `exec --json` stream as of 0.142) — precise `used_percent` per window | — | `RateLimitObserved` (one per window; `UsedPercent` when the CLI reports one) |

The adapters are public — you can call
[`ClaudeEventAdapter.Map(line, runId)`](../src/CodingAgentRunner/Adapters/ClaudeEventAdapter.cs)
(and the `Codex` / `Gemini` equivalents) directly to turn one output line into events
without spawning a process. That is what the parsing tests and benchmarks use.

## The asymmetries the normalized model absorbs

The three dialects do not differ only in spelling. The normalized model has to absorb
real structural differences that a hand-rolled per-CLI parser would have to handle
case by case:

1. **Turn granularity.** Claude packs a whole turn into one `content` array; Codex
   scatters the same turn across many `item.*` frames. The event stream makes both
   look the same to a consumer.
2. **Richness gaps.** Only Claude emits an init frame; only Claude and Codex emit
   thinking and rate-limit data (Claude live in the run stream, Codex only in its
   rollout logs / app-server protocol); Antigravity has no plan/TODO. A concept a
   CLI lacks is simply an event that never arrives — an empty cell, not an error.
3. **Implicit tools.** Codex has no explicit "read file" or "search" tool; those go
   through shell commands. The same intent surfaces as a different frame.
4. **Token vocabulary.** The cached-token field alone has three names —
   `cache_read_input_tokens` (Claude), `cached_input_tokens` (Codex), `cached`
   (Gemini) — and only Codex reports a `reasoning_output_tokens` count. A usage parser
   that knows one dialect silently drops tokens on the others.
   [`UsageSummaryParser`](../src/CodingAgentRunner/Metrics/UsageSummaryParser.cs)
   folds all of them into one `UsageTokens`.
5. **Text form.** Text is an array of blocks in one dialect and a bare string in
   another; tool arguments are `input` here and `parameters` there.

## One closed vocabulary

`CliRunEvent` is a **closed sum type** — a fixed set of `record` cases
(`RunStarted`, `SessionStarted`, `OutputDelta`, `ToolStarted`, `ToolCompleted`,
`PlanUpdated`, `Heartbeat`, `TurnCompleted`, `TurnFailed`, `RateLimitObserved`,
`Interrupt`, `RunEnded`, `Unknown`, …), not an open string-keyed bag. A `switch` over
it is checked for exhaustiveness by the compiler, so adding a case turns into a
build error at every consumption site rather than a silent fallback. A frame the
adapter does not recognize becomes `CliRunEvent.Unknown` with a capped sample of the
raw line — unrecognized input is surfaced, never silently dropped.

## Two projections, both from the library

The same parse feeds two shipped projections, so you consume whichever fits the job:

- **The event stream** ([`CliRunEvent`](../src/CodingAgentRunner/Events/CliRunEvent.cs))
  — the live channel for the watchdog, phase tracking, stop decisions, and metrics
  (every event carries an `ObservedAt`).
- **The render line/span model** (the optional
  [`CodingAgentRunner.Rendering`](../src/CodingAgentRunner.Rendering) package) —
  `RenderedLine` / `RenderedSpan` for displaying a turn, presentation-agnostic so the
  same model maps to HTML, an ANSI terminal, or a UI framework. See
  [Architecture › Rendering](architecture.md).

Both come from the library. Neither asks the consumer to re-parse stdout.

## Parse by frame type, never by model

Parsing is driven by the structural frame type of each line, never by which model
produced it. The model travels only as metadata alongside the events. So the parser
does not need a new branch when a CLI ships a new model, and the same `CliRunEvent`
stream is what the watchdog, the metrics recorder, and any renderer consume.

## Scope

This model presupposes a **structured wire protocol** — NDJSON / JSON frames on
stdout. A CLI that is PTY/TUI-only, with no machine-readable output to adapt, does
not fit it; that is the one shape CodingAgentRunner deliberately does not cover.

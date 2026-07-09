# Decision Log

Decisions that shaped the codebase, each stated as *what* was decided and *why*,
sourced from the repo, its README, or its git history. New decisions go at the
top. This is a seed, not a complete history.

---

### The platform owns git; the guard is defence-in-depth, not a sandbox
The reliable layer is a soft rule in the agent's instruction files
(`AGENTS.md` / `CLAUDE.md`): don't run git — the host owns commit and push. On
top, an optional PATH-front `git` wrapper (`AgentGitCommandGuard`,
`GitGuardOptions`) blocks mutating subcommands. This is explicitly **not** a hard
guarantee: a CLI that controls its own shell's PATH can resolve the real `git`
and bypass it. Chosen as an extra safety net, not a jail.
_Source: README "Why"; `Abstractions/CliOptions.cs` `GitGuardOptions`;
`Execution/Hardening/AgentGitCommandGuard.cs`._

### Covering a CLI is data, not a subclass — one engine, a descriptor catalog
Each CLI is a `CliDescriptor` (data plus a few pure delegates) in a fixed
`CliCatalog`, and one `internal sealed` engine (`CliRunEngine`) is parameterized
by it. There is no consumer plug-in / "add your own CLI" extension point; the
library is purpose-built for its known CLIs. Chosen so run logic is written once
and the CLIs stay a closed, tested set.
_Source: README "Supported agents"; `Execution/CliDescriptor.cs`,
`CliCatalog.cs`, `CliRunEngine.cs`; git `91c6686` (descriptor architecture)._

### Completion comes from the CLI's own signal, not a `[[TASK_DONE]]` scrape
A run is considered finished from the CLI's own `stream-json` result frame plus
process exit — not from scraping a sentinel token out of the output. Chosen
because sentinel-scraping is fragile.
_Source: README "Why"; `docs/process-termination.md`._

### Three-valued outcome: a deliberate stop is not a crash
`RunEnded` carries `completed` / `stopped` / `failed`, classified from exit code
plus stop reason (`RunStatusClassifier`). A user pause or watchdog stop is
reported as `stopped`, never as a `-1` crash — the distinction Windows'
`Process.Kill` throws away. Chosen to keep outcomes honest.
_Source: README "Why"; `docs/process-termination.md`._

### Gemini deprecated, Antigravity is the Google path, Copilot removed
Gemini's surface is marked `[Obsolete]` (removal planned before 1.0); Antigravity
(`agentapi`) is the maintained Google integration; Copilot was removed because
its headless path was PTY/TUI-dependent and didn't fit the structured stream
engine. Chosen to keep the supported set to CLIs that fit the hardened engine.
_Source: README "Supported agents"; git `8d612e5` (deprecate Gemini)._

### Rendering is a separate, opt-in, one-way-dependent package
`CodingAgentRunner.Rendering` depends on core; core never references it, so
event-stream consumers don't pay for it. Its default link resolver
(`LinkExtractor.WebDefault`) enforces an http/https/mailto allowlist that rejects
`javascript:` / `data:` targets, making HTML output XSS-safe by default.
_Source: README "Optional rendering package"; git `99de53e`._

### The machine-global quota cache lives in the OS-native app-data directory
The shared quota cache file is stored under the OS app-data location
(`%LOCALAPPDATA%\coding-agent-runner`, `~/Library/Application Support`,
`~/.local/share`), overridable via `CODING_AGENT_RUNNER_CACHE_DIR`, so every
process that opts in shares one cache instead of re-probing.
_Source: README "Quota with escalation caching"; git `0dca3ce`._

---

## On the suggested seed decisions that don't apply here

The onboarding card suggested two Decision Log seeds — "one task per invocation"
and "server URL via env." Neither is a decision in this repository: it is an
in-process library, not a worker process that runs one task per invocation or
talks to a server. They are recorded here as *not applicable* rather than
invented into the log. See [Current Development State](current-development-state.md).

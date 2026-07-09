# Environment-driven configuration

> Honest counterpart of the onboarding card's "env-driven `RunnerOptions`" topic.
> There is no `RunnerOptions` type. Configuration comes from a `CliOptions` record
> the consumer supplies, plus a small set of environment variables the library
> reads and writes. This page describes what actually configures a run.

## `CliOptions` — the consumer's configuration record

`src/CodingAgentRunner/Abstractions/CliOptions.cs`. Its own doc comment calls it
"consumer-supplied configuration for the runner." Everything is optional with
sane defaults. Notable members:

- `ClaudePath` / `CodexPath` / `AntigravityPath` / `GeminiPath` — explicit
  path/command per CLI; the CLI name on `PATH` is used when none is given.
  (`GeminiPath` is `[Obsolete]`.)
- `EnvironmentOverrides` — extra environment variables applied to **every**
  spawned CLI process.
- `AllowAgentGitMutation` — when true, the git guard is disabled.
- `GitGuard` (`GitGuardOptions`) — the git-guard configuration (env prefix,
  forbidden commands, block message).
- `Hardening` (`CliHardeningOptions`) — `DenyStdin` and `EnforceUtf8` toggles.
- `Spawner` (`ICliProcessSpawner`) — an optional custom process launcher; null
  uses redirected pipes.

## Environment variables the library reads

The library treats specific environment variables as inputs, so a host can
configure behaviour without code:

- `CODING_AGENT_RUNNER_CACHE_DIR` — overrides the OS-native app-data location of
  the machine-global quota cache.
- `CLAUDE_CONFIG_DIR` / `CODEX_HOME` — point a CLI at an isolated config home;
  also read by the quota probes to find the CLI's config.
- `USERPROFILE` / `HOME` — resolved by the default user-home provider
  (`IUserHomeProvider`).
- `PATH` / `PATHEXT` — used by binary resolution (`.cmd` → `.exe`) and the git
  guard.
- The git guard derives its own env-var names from `GitGuardOptions.EnvPrefix`
  (default `CODING_AGENT_RUNNER`) for its allow / real-git / guard-dir signals.

## Why it is shaped this way

`CliOptions` "replaces the host application's ambient configuration" — the point
of the extraction was to stop the library depending on Agent Studio's config
system. A consumer wires one options record; the environment variables above are
the few ambient inputs the library still reads, each for a concrete
platform/isolation reason.

_Sources: `Abstractions/CliOptions.cs`; `Abstractions/IUserHomeProvider.cs`;
`Quota/IQuotaCacheStore.cs`, `Quota/ClaudeOAuthUsageProbe.cs`,
`Quota/CodexSessionLogProbe.cs`; `Execution/Hardening/BinaryResolver.cs`,
`AgentGitCommandGuard.cs`; README "Quota with escalation caching"._

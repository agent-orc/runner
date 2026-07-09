# Run isolation and the platform-owns-git boundary

> Honest counterpart of the onboarding card's "runner lease/fencing" topic. There
> is no distributed lease or fencing token in this library — it runs local child
> processes in-process. What occupies that "keep runs from stepping on each other
> and on the host" role is **clean-context isolation** plus the **platform-owns-git
> boundary**. This page covers both.

## Clean-context isolation

A run can use one of two context modes for the CLI's persistent home/state (not
the repo or prompt):

- **shared** — the operator's normal signed-in CLI home (settings, cache, memory,
  prior state).
- **clean** — a temporary per-run CLI home seeded with only the minimum auth/base
  config, so concurrent runs (and the operator's own interactive session) don't
  collide.

The recipe for a clean home is declared as data on each CLI's descriptor via
`CleanContextSpec` (`src/CodingAgentRunner/Execution/CleanContextSpec.cs`):

- `EnvVar` — the environment variable that points the CLI at the isolated home
  (`CLAUDE_CONFIG_DIR` for Claude, `CODEX_HOME` for Codex).
- `SourceConfigDirName` — the user-home-relative dir to seed from (`.claude`,
  `.codex`).
- `SeedFiles` — the files copied in: auth + base config only, never history or
  memory.

Declaring the recipe as data keeps the descriptor a pure value; the engine turns
it into the actual per-run home at spawn time
(`Execution/CleanContextPreparation.cs`). Repo files and repo instruction files
(`AGENTS.md` / `CLAUDE.md`) are visible in both modes because they come from the
checkout, not from the CLI home.

## The platform-owns-git boundary

The host application owns version control; worker agents must not run mutating
git. This is enforced in two layers, and the distinction matters:

1. **The reliable layer is a soft rule** in the agent's instruction files
   (`AGENTS.md` / `CLAUDE.md`): "don't run git; the host owns commit and push."
2. **On top, an optional PATH-front `git` wrapper** (`AgentGitCommandGuard`,
   configured by `GitGuardOptions` on `CliOptions`) blocks mutating subcommands
   — the forbidden set includes `commit`, `push`, `reset`, `checkout`, `branch`,
   `stash`, and others. Setting `AllowAgentGitMutation` disables the guard.

The wrapper is **defence-in-depth, not a sandbox**: a CLI that controls its own
shell's PATH (for example claude-code's bash) can resolve the real `git` and
bypass it. Treat it as an extra safety net, not a hard guarantee. The
corresponding decision is in the [Decision Log](../decision-log.md).

_Sources: `Abstractions/CliOptions.cs` (`GitGuardOptions`, `AllowAgentGitMutation`);
`Execution/Hardening/AgentGitCommandGuard.cs`; `Execution/CleanContextSpec.cs`;
`Execution/CleanContextPreparation.cs`; README "Why" and "Supported agents"._

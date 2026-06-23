# Why Windows hardening

Running a coding-agent CLI from another process *looks* trivial — until you do it
on Windows, at scale, unattended. Each behaviour below cost real debugging time in
a production orchestrator. CodingAgentRunner encodes the fix, and each ships with a
test so the reason can't be quietly refactored away.

---

## 1 · The `.cmd`-shim prompt truncation

**Symptom.** The agent starts, explores aimlessly, then asks for input or escalates
— as if it never got a task. Every run. Nothing completes.

**Cause.** On Windows, `claude` / `codex` on the `PATH` are usually npm **`.cmd`
shims** (no bare `.exe`). Launching a `.cmd` runs it through `cmd.exe /c …`, and
`cmd.exe` treats the newline inside a **multi-line `-p <prompt>` argument as a
command separator** — so the agent receives only the **first line** of the prompt
and never the task body. (The delivered prompt was literally ~22 characters where
it should have been thousands.)

**Fix.** Resolve the shim to the **real `.exe`** it points at and launch that
directly, so `CreateProcess` parses the argument via `CommandLineToArgvW` and the
multi-line prompt survives verbatim.

> Lesson: on Windows, never pass a multi-line argument to a `.cmd`/`.bat`.

---

## 2 · The platform-owns-git boundary

**Symptom.** Worker agents committing/pushing on their own — and a class of mid-run
crashes that fired right after a slow `git commit`.

**Cause + nuance.** A guard is supposed to forbid the agent from running
`git commit`/`push` (the host owns version control). The first implementation
shipped only a `git.cmd` wrapper on `PATH` — but the agent invokes `git` through
its **git-bash** shell, which resolves a bare `git` to the real `/mingw64/bin/git`,
never a `.cmd`. So the guard was silently bypassed (the *same* `.cmd` blind spot as
#1). Two layers close it: an explicit instruction in the agent's rules **and** an
extensionless `git` wrapper that git-bash actually resolves.

**Fix.** The host owns commit/push; the agent just edits. Removing the agent's slow
`git` subprocess also removed a crash trigger.

---

## 3 · Clean-context isolation

**Symptom.** Concurrent runs — or a run and your own interactive CLI session —
stepping on each other's session/state.

**Fix.** Each run gets an **isolated CLI home** (e.g. via the CLI's config-dir
environment variable) under a temp path, so sessions, credentials caches, and
transcripts never collide. The liveness watcher follows the run into that home.

---

## 4 · Why completion comes from the CLI, not a sentinel

**The trap.** A natural-looking shortcut is to detect "the agent is done" by
scraping a terminal marker like `[[TASK_DONE]]` from the output. It is fragile: a
scanner that matches the marker on **every** line also fires on file content the
agent merely **read** (tool results ride the `user` stream, and rules / contract
docs are full of such literals) — killing a run mid-work as a false "completion".

**The fix: don't scrape.** The CLI already tells you when it's done. claude-code's
`stream-json` emits a final `result` frame and the process exits; codex signals
completion through its SDK / app-server protocol. CodingAgentRunner reports
completion from **that** real signal (`TurnCompleted` / `ProcessExited`).

**Boundary.** A structured outcome vocabulary (`done` / `blocked` / `needs-input`)
is an *application* run-protocol, not a universal CLI primitive — so it lives in
the consumer app, not in this library.

---

## 5 · Process-tree reaping & the slow-Bash exit (open)

**Symptom.** Orphaned grandchildren (e.g. a node dev-server) holding worktree file
handles after a run; and — under investigation — a CLI process self-exiting with
`exit=-1` during or after a long-running child subprocess, only under the host's
redirected-pipe spawn (a standalone launch survives).

**Direction.** Reap the **whole process tree** on stop, and (for the exit-1 class)
spawn via a curated-handle path so a long-running grandchild's teardown can't take
the parent with it. This one is honest WIP — documented so the next person doesn't
start from zero.

---

## 6 · Why quota probing is cached (with escalation)

**Symptom.** Checking "how much quota is left?" too often hammers the CLI — each
probe **spawns the CLI** for tens of seconds.

**Fix.** Cache the per-CLI remaining-quota snapshot (default TTL 10 min) with
**stale-while-revalidate** + per-CLI coalescing, and an **escalation policy**: the
effective TTL shortens as usage approaches the cap (e.g. ≥90 % → 2 min, ≥97 % →
30 s — thresholds and intervals configurable). Far from the limit you barely probe;
near it you stay fresh.

# System Knowledge

Durable "how this part works and why" pages — the mechanism and the reasoning
behind a specific behaviour, not the full API reference (that is
[../../architecture.md](../../architecture.md)).

These three seed pages were chosen as the honest counterparts of the topics the
onboarding card suggested (runner lease/fencing, env-driven `RunnerOptions`,
artifact/log shipping). This repository is an in-process library rather than a
distributed worker, so each page describes the real mechanism that occupies the
same role:

- **[Run isolation and the platform-owns-git boundary](run-isolation-and-git-boundary.md)**
  — how concurrent runs stay from colliding and how the git boundary is enforced.
  (In place of "lease/fencing" — there is no distributed lease here.)
- **[Environment-driven configuration](environment-driven-configuration.md)** —
  how `CliOptions` and environment variables configure the runner.
  (The real shape behind "env-driven `RunnerOptions`".)
- **[Durable run logs](durable-run-logs.md)** — how per-run, per-stream output
  logs are written and read.
  (In place of "artifact/log shipping" — logs are durable and local, not shipped
  to a server.)

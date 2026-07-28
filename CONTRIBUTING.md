# Contributing to CodingAgentRunner

CodingAgentRunner is pre-1.0, so its public surface can still change. Issues,
ideas, and pull requests are welcome.

## Build & test

Install the .NET 10 SDK, clone the repository, and run these commands from the
repository root:

```bash
dotnet restore
dotnet build --no-restore
dotnet test --no-build
```

Before submitting a pull request, run the test suite on the operating systems
affected by the change. CI runs on Windows and Linux.

## Conventions

- C# with nullable reference types enabled; `LangVersion` `latest`.
- Keep the library dependency-light — prefer `Microsoft.Extensions.*.Abstractions`
  (e.g. `ILogger`) over concrete dependencies, and an options object over ambient config.
- Every hardening behaviour ships with a test that pins why it exists — these
  behaviours were learned the hard way; the tests are the institutional memory.
- Conventional-commit style messages are appreciated.

Start with [AGENTS.md](AGENTS.md) for the project rules. The design and public API
conventions are documented in [docs/architecture.md](docs/architecture.md), and
the hardening constraints are explained in
[docs/why-windows-hardening.md](docs/why-windows-hardening.md).

## Scope

CodingAgentRunner is the *process + protocol* layer for coding-agent CLIs:
spawn, isolate, stream, supervise, quota. It deliberately does **not** include
task/lane/pipeline orchestration — that belongs in the application on top.

## Agent-driven maintenance

The agent-orc organization uses agent-driven pipelines, so some repository
changes may land without a conventional human-authored pull request. Human
issues and pull requests are still welcome and are reviewed against the same
tests, scope, and project conventions.

## Pull requests

- Keep changes focused and explain the behavior or problem they address.
- Add or update tests when behavior changes.
- Update user-facing documentation when the public API or setup changes.
- Do not remove a hardening guard without reading its rationale and tests.

By participating, you agree to follow the
[Code of Conduct](CODE_OF_CONDUCT.md). Report security issues through the
private process in [SECURITY.md](SECURITY.md), not through a public issue.

## License

By contributing you agree that your contributions are licensed under the
[Apache-2.0](LICENSE) license.

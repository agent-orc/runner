# Contributing to CodingAgentRunner

Thanks for your interest! This library is being actively **extracted and
generalized** from a production orchestrator, so the public surface is still
moving. Early issues, ideas, and PRs are very welcome.

## Build & test

```bash
dotnet build
dotnet test
```

The .NET 10 SDK is required.

## Conventions

- C# with nullable reference types enabled; `LangVersion` `latest`.
- Keep the library **dependency-light** — prefer `Microsoft.Extensions.*.Abstractions`
  (e.g. `ILogger`) over concrete dependencies, and an options object over ambient config.
- Every hardening behaviour ships with a **test that pins why it exists** — these
  behaviours were learned the hard way; the tests are the institutional memory.
- Conventional-commit style messages are appreciated.

## Scope

CodingAgentRunner is the *process + protocol* layer for coding-agent CLIs:
spawn, isolate, stream, supervise, quota. It deliberately does **not** include
task/lane/pipeline orchestration — that belongs in the application on top.

## License

By contributing you agree that your contributions are licensed under the
[Apache-2.0](LICENSE) license.

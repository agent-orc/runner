# CLI stream parsing fixtures

These JSONL captures model the content shapes involved in incident 2417. Each
supported CLI has its own native stream frame shape. The shared expected file
contains the exact visible payloads that every adapter must emit.

The fixture matrix covers source code, diffs, HTML, Markdown, an image reference,
nested JSON, ANSI-coloured UTF-8 logs, and a physical JSONL line longer than
16 KiB. Terminal-looking strings inside content are data. Only the final native
CLI completion frame may produce `TurnCompleted`.

The suite is not part of the default fast test run. Run it explicitly with:

```powershell
$env:CODING_AGENT_RUNNER_STREAM_FIXTURES = '1'
dotnet test --filter Category=StreamParsingFixtures
```

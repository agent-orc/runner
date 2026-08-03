# Process-spawner composition

`CliOptions.Spawner` receives a `ProcessStartInfo` after the runner has resolved
the executable and applied its launch policy. The supported default is
`CliProcessSpawner.Default`: it uses the curated Windows handle-list spawn and
its process-tree kill override on Windows, and normal redirected-pipe spawning on
other platforms.

To inspect, validate, or add to that prepared launch, use
`CliProcessSpawner.Decorate`. The callback runs before the default spawner.

```csharp
using CodingAgentRunner.Abstractions;

var options = new CliOptions
{
    Spawner = CliProcessSpawner.Decorate(startInfo =>
    {
        if (!Path.IsPathFullyQualified(startInfo.FileName))
            throw new InvalidOperationException("CLI executable must be absolute.");

        startInfo.Environment["HOST_RUN_ID"] = runId;
    }),
};
```

Do not replace `UseShellExecute` or the standard-stream redirect settings in a
decorator. The runner prepares those values so it can stream output and maintain
its stdin policy. A host that needs a different transport, such as a PTY, can
still implement `ICliProcessSpawner`; that implementation owns its streams and
optional `CliSpawn.KillOverride`.

This seam was added for [Agent Studio AGT-2371](https://linear.app/agent-studio/issue/AGT-2371).

using System.Diagnostics;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Tests.Execution;

public class CliProcessSpawnerTests
{
    [Fact]
    public async Task Decorate_DefaultSpawn_PreservesCrossPlatformPipeLaunch()
    {
        ProcessStartInfo? observed = null;
        var spawner = CliProcessSpawner.Decorate(psi =>
        {
            observed = psi;
            psi.Environment["CAR_COMPOSITION_TEST"] = "set";
        });
        var spawn = spawner.Spawn(StartInfo(redirectStdin: false));

        Assert.Equal("set", observed!.Environment["CAR_COMPOSITION_TEST"]);
        await spawn.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        spawn.Stdout.Dispose();
        spawn.Stderr.Dispose();
        spawn.Process.Dispose();
    }

    [Fact]
    public void Decorate_PreparesTheOriginalStartInfo_ThenDelegates()
    {
        var inner = new RecordingSpawner();
        var spawner = CliProcessSpawner.Decorate(psi =>
        {
            Assert.True(psi.RedirectStandardOutput);
            psi.Environment["CAR_COMPOSITION_TEST"] = "set";
        }, inner);
        var startInfo = StartInfo(redirectStdin: false);

        _ = spawner.Spawn(startInfo);

        Assert.Same(startInfo, inner.StartInfo);
        Assert.Equal("set", inner.StartInfo!.Environment["CAR_COMPOSITION_TEST"]);
    }

    [Fact]
    public void Default_StdinDenied_UsesNullStream()
    {
        var spawn = CliProcessSpawner.Default.Spawn(StartInfo(redirectStdin: false));

        Assert.Same(Stream.Null, spawn.Stdin);
        spawn.Process.WaitForExit(TimeSpan.FromSeconds(30));
        spawn.Stdout.Dispose();
        spawn.Stderr.Dispose();
        spawn.Process.Dispose();
    }

    [Fact]
    public void Default_RedirectedStdin_ProvidesWritablePipe()
    {
        var spawn = CliProcessSpawner.Default.Spawn(StartInfo(redirectStdin: true));

        Assert.NotSame(Stream.Null, spawn.Stdin);
        spawn.Stdin.Close();
        spawn.Process.WaitForExit(TimeSpan.FromSeconds(30));
        spawn.Stdout.Dispose();
        spawn.Stderr.Dispose();
        spawn.Process.Dispose();
    }

    [Fact]
    public void Default_WindowsSpawn_ProvidesTreeKillOverride()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawn = CliProcessSpawner.Default.Spawn(StartInfo(redirectStdin: false));

        Assert.NotNull(spawn.KillOverride);
        spawn.KillOverride!(RunStopReason.UserStop);
        spawn.Process.WaitForExit(TimeSpan.FromSeconds(30));
        spawn.Stdout.Dispose();
        spawn.Stderr.Dispose();
        spawn.Process.Dispose();
    }

    private static ProcessStartInfo StartInfo(bool redirectStdin) => new("dotnet", "--version")
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardInput = redirectStdin,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private sealed class RecordingSpawner : ICliProcessSpawner
    {
        public ProcessStartInfo? StartInfo { get; private set; }

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            var process = Process.GetCurrentProcess();
            return new CliSpawn(
                process,
                Stream.Null,
                new StreamReader(Stream.Null),
                new StreamReader(Stream.Null));
        }
    }
}

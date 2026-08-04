using System.Diagnostics;
using CodingAgentRunner.Abstractions;
using Xunit;

namespace CodingAgentRunner.Tests.Execution;

public class CliProcessSpawnerTests
{
    [Fact]
    public void DefaultSpawner_UsesCuratedWindowsLaunch_WithDeniedStdinAndKillOverride()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawn = DefaultCliProcessSpawner.Instance.Spawn(DotnetVersionStartInfo(redirectStdin: false));
        try
        {
            Assert.Same(Stream.Null, spawn.Stdin);
            Assert.NotNull(spawn.KillOverride);
            Assert.Contains(".", spawn.Stdout.ReadToEnd());
            spawn.Process.WaitForExit();
            Assert.Equal(0, spawn.Process.ExitCode);
        }
        finally
        {
            spawn.Process.Dispose();
            spawn.Stdout.Dispose();
            spawn.Stderr.Dispose();
        }
    }

    [Fact]
    public void DefaultSpawner_UsesCuratedWindowsLaunch_WithRedirectedStdin()
    {
        if (!OperatingSystem.IsWindows()) return;

        var spawn = DefaultCliProcessSpawner.Instance.Spawn(DotnetVersionStartInfo(redirectStdin: true));
        try
        {
            Assert.NotSame(Stream.Null, spawn.Stdin);
            spawn.Stdin.Close();
            spawn.Process.WaitForExit();
            Assert.Equal(0, spawn.Process.ExitCode);
        }
        finally
        {
            spawn.Process.Dispose();
            spawn.Stdin.Dispose();
            spawn.Stdout.Dispose();
            spawn.Stderr.Dispose();
        }
    }

    [Fact]
    public void Decorator_CanMutatePreparedStartInfo_AndDelegateToCarDefault()
    {
        ProcessStartInfo? observed = null;
        var spawner = new DecoratingSpawner(startInfo =>
        {
            startInfo.Environment["CAR_SPAWNER_TEST"] = "set";
            observed = startInfo;
        });

        var spawn = spawner.Spawn(DotnetVersionStartInfo(redirectStdin: false));
        try
        {
            Assert.Same("set", observed!.Environment["CAR_SPAWNER_TEST"]);
            spawn.Process.WaitForExit();
            Assert.Equal(0, spawn.Process.ExitCode);
        }
        finally
        {
            spawn.Process.Dispose();
            spawn.Stdout.Dispose();
            spawn.Stderr.Dispose();
        }
    }

    [Fact]
    public void DefaultSpawner_HasRedirectedPipeParity_OnNonWindows()
    {
        if (OperatingSystem.IsWindows()) return;

        var spawn = DefaultCliProcessSpawner.Instance.Spawn(DotnetVersionStartInfo(redirectStdin: false));
        try
        {
            Assert.Same(Stream.Null, spawn.Stdin);
            Assert.Null(spawn.KillOverride);
            spawn.Process.WaitForExit();
            Assert.Equal(0, spawn.Process.ExitCode);
        }
        finally
        {
            spawn.Process.Dispose();
            spawn.Stdout.Dispose();
            spawn.Stderr.Dispose();
        }
    }

    private static ProcessStartInfo DotnetVersionStartInfo(bool redirectStdin) => new(ResolveDotnet(), "--version")
    {
        UseShellExecute = false,
        RedirectStandardInput = redirectStdin,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };

    private static string ResolveDotnet()
    {
        if (!OperatingSystem.IsWindows()) return "dotnet";

        foreach (var root in new[] { Environment.GetEnvironmentVariable("DOTNET_ROOT"), Environment.GetEnvironmentVariable("DOTNET_ROOT(x86)") })
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var candidate = Path.Combine(root, "dotnet.exe");
            if (File.Exists(candidate)) return candidate;
        }

        var where = Path.Combine(Environment.SystemDirectory, "where.exe");
        using var lookup = Process.Start(new ProcessStartInfo(where, "dotnet")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        var resolved = lookup?.StandardOutput.ReadLine();
        lookup?.WaitForExit();
        return !string.IsNullOrWhiteSpace(resolved)
            ? resolved.Trim()
            : throw new InvalidOperationException("Could not resolve dotnet.exe for the Windows spawn test.");
    }

    private sealed class DecoratingSpawner(Action<ProcessStartInfo> decorate) : ICliProcessSpawner
    {
        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            decorate(startInfo);
            return DefaultCliProcessSpawner.Instance.Spawn(startInfo);
        }
    }
}

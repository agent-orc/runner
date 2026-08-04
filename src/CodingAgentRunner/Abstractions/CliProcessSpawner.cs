using System.Diagnostics;
using System.Runtime.Versioning;
using System.Text;
using CodingAgentRunner.Execution.Hardening;
using CodingAgentRunner.Execution.Win;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Abstractions;

/// <summary>
/// A spawned child process plus its pipes, as a custom <see cref="ICliProcessSpawner"/>
/// hands it back to the engine. Mirrors what the built-in pipe-redirection spawn
/// produces, so the engine treats a custom spawn (e.g. a Windows pseudo-terminal)
/// identically.
/// </summary>
/// <param name="Process">The started child process.</param>
/// <param name="Stdin">Writable stdin stream (or <see cref="Stream.Null"/> when stdin is denied).</param>
/// <param name="Stdout">Reader for the child's stdout.</param>
/// <param name="Stderr">Reader for the child's stderr (a PTY spawn may merge stderr into stdout and pass a never-emitting reader here).</param>
/// <param name="KillOverride">Optional custom termination (e.g. close the PTY); when null the engine kills the process tree.</param>
public sealed record CliSpawn(
    Process Process,
    Stream Stdin,
    StreamReader Stdout,
    StreamReader Stderr,
    Action<RunStopReason>? KillOverride = null);

/// <summary>
/// Pluggable process spawner. Inject one via <see cref="CliOptions.Spawner"/> to change
/// how the engine launches a CLI — the canonical use is a <b>Windows pseudo-terminal</b>
/// spawner so a Node CLI flushes <c>stdout</c> per newline (block-buffered pipes otherwise
/// hide live output and trip the silence watchdog). When no spawner is set the engine uses
/// <see cref="DefaultCliProcessSpawner"/>.
/// <para>The engine has already built the <see cref="ProcessStartInfo"/> (binary, argv,
/// environment hardening, working directory, redirect flags) — the spawner only chooses
/// the launch mechanism.</para>
/// </summary>
public interface ICliProcessSpawner
{
    /// <summary>Launch the prepared <paramref name="startInfo"/> and return the child + pipes.</summary>
    CliSpawn Spawn(ProcessStartInfo startInfo);
}

/// <summary>
/// CAR's supported pipe-based process spawner. On Windows it uses CAR's curated
/// handle-inheritance launch; elsewhere it has the same redirected-pipe behaviour
/// as <see cref="Process.Start(ProcessStartInfo)"/>.
/// <para>
/// Use <see cref="Instance"/> from an <see cref="ICliProcessSpawner"/> decorator
/// when a host needs to inspect or change a prepared <see cref="ProcessStartInfo"/>
/// without replacing CAR's launch hardening.
/// </para>
/// </summary>
public sealed class DefaultCliProcessSpawner : ICliProcessSpawner
{
    /// <summary>The shared, stateless default spawner used when <see cref="CliOptions.Spawner"/> is null.</summary>
    public static DefaultCliProcessSpawner Instance { get; } = new();

    private DefaultCliProcessSpawner() { }

    /// <summary>
    /// Starts a prepared process. The Windows path passes only the standard-stream
    /// handles to the child and returns CAR's process-tree kill action.
    /// </summary>
    public CliSpawn Spawn(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (OperatingSystem.IsWindows())
            return SpawnWindows(startInfo);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        var stdin = startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null;
        return new CliSpawn(process, stdin, process.StandardOutput, process.StandardError);
    }

    [SupportedOSPlatform("windows")]
    private static CliSpawn SpawnWindows(ProcessStartInfo startInfo)
    {
        var executable = BinaryResolver.ResolveExecutable(startInfo.FileName);
        var result = WindowsHandleScrubSpawner.Spawn(
            executable,
            startInfo.ArgumentList.ToArray(),
            startInfo.WorkingDirectory,
            startInfo.Environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value, StringComparer.OrdinalIgnoreCase),
            startInfo.RedirectStandardInput);
        result.Process.EnableRaisingEvents = true;
        return new CliSpawn(
            result.Process,
            result.Stdin ?? Stream.Null,
            new StreamReader(result.Stdout, Encoding.UTF8),
            new StreamReader(result.Stderr, Encoding.UTF8),
            _ => result.KillTree());
    }
}

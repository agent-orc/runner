using System.Diagnostics;
using System.Runtime.Versioning;
using CodingAgentRunner.Model;
using CodingAgentRunner.Execution.Win;

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
/// <see cref="CliProcessSpawner.Default"/>.
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
/// Built-in <see cref="ICliProcessSpawner"/> implementations and composition helpers.
/// </summary>
public static class CliProcessSpawner
{
    /// <summary>
    /// The runner's normal launch path. On Windows it starts the child with the
    /// curated handle list used by the runner; on other platforms it uses
    /// <see cref="Process.Start()"/> with the prepared redirected pipes.
    /// </summary>
    public static ICliProcessSpawner Default { get; } = new DefaultCliProcessSpawner();

    /// <summary>
    /// Creates a spawner that calls <paramref name="prepare"/> with the fully prepared
    /// launch, then delegates spawning to <paramref name="inner"/> or the runner default.
    /// Use this to add host settings or reject a launch without replacing the runner's
    /// Windows handle scrubbing, pipe setup, or termination behaviour.
    /// </summary>
    public static ICliProcessSpawner Decorate(
        Action<ProcessStartInfo> prepare,
        ICliProcessSpawner? inner = null)
        => new DelegatingCliProcessSpawner(prepare, inner ?? Default);
}

/// <summary>
/// Calls a host preparation callback and delegates the actual spawn to another
/// <see cref="ICliProcessSpawner"/>. The default inner spawner is
/// <see cref="CliProcessSpawner.Default"/>.
/// </summary>
public sealed class DelegatingCliProcessSpawner : ICliProcessSpawner
{
    private readonly Action<ProcessStartInfo> _prepare;
    private readonly ICliProcessSpawner _inner;

    /// <summary>Create a decorating spawner.</summary>
    public DelegatingCliProcessSpawner(Action<ProcessStartInfo> prepare, ICliProcessSpawner? inner = null)
    {
        _prepare = prepare ?? throw new ArgumentNullException(nameof(prepare));
        _inner = inner ?? CliProcessSpawner.Default;
    }

    /// <inheritdoc />
    public CliSpawn Spawn(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _prepare(startInfo);
        return _inner.Spawn(startInfo);
    }
}

/// <summary>
/// The runner's default pipe-based process spawn. This is public so a host can
/// compose with, rather than replace, the supported launch path.
/// </summary>
public sealed class DefaultCliProcessSpawner : ICliProcessSpawner
{
    /// <inheritdoc />
    public CliSpawn Spawn(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        if (OperatingSystem.IsWindows())
            return SpawnWindows(startInfo);

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.Start();
        return new CliSpawn(
            process,
            startInfo.RedirectStandardInput ? process.StandardInput.BaseStream : Stream.Null,
            process.StandardOutput,
            process.StandardError);
    }

    [SupportedOSPlatform("windows")]
    private static CliSpawn SpawnWindows(ProcessStartInfo startInfo)
    {
        var result = WindowsHandleScrubSpawner.Spawn(
            startInfo.FileName,
            startInfo.ArgumentList.ToArray(),
            startInfo.WorkingDirectory,
            startInfo.Environment.ToDictionary(pair => pair.Key, pair => (string?)pair.Value),
            startInfo.RedirectStandardInput);
        return new CliSpawn(
            result.Process,
            result.Stdin ?? Stream.Null,
            new StreamReader(result.Stdout),
            new StreamReader(result.Stderr),
            _ => result.KillTree());
    }
}

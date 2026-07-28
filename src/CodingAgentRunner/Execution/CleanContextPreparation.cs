using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Execution;

/// <summary>
/// One run's <b>clean context</b>: a freshly created, per-run config home for a
/// CLI plus the env override that points the CLI at it. Owned by the run;
/// disposing it tears the temp home down.
/// <para>
/// "clean" is not a CLI flag — it is the absence of the operator's accumulated
/// state. Each adapter implements it by relocating the CLI's whole config home
/// (Claude <c>CLAUDE_CONFIG_DIR</c>, Codex <c>CODEX_HOME</c>) to this temp dir,
/// into which only the auth + base config are seeded. Session history, memory, and
/// project state are deliberately left behind so the run sees only the prompt plus
/// the versioned repo files. Repo instruction files (<c>AGENTS.md</c> /
/// <c>CLAUDE.md</c>) are loaded from the checkout, not the home, so they stay
/// active regardless of mode.
/// </para>
/// </summary>
internal sealed class CleanContextPreparation : IDisposable
{
    private readonly ILogger? _logger;
    private int _disposed;

    /// <summary>Create a clean-context handle around an already-prepared temp home.</summary>
    public CleanContextPreparation(
        string cliType,
        string tempHome,
        IReadOnlyDictionary<string, string> envOverrides,
        IReadOnlyList<CliContextSource> sources,
        ILogger? logger = null)
    {
        CliType = cliType;
        TempHome = tempHome;
        EnvOverrides = envOverrides;
        Sources = sources;
        _logger = logger;
    }

    /// <summary>The CLI this clean home was prepared for (one of <see cref="CliTypes"/>).</summary>
    public string CliType { get; }

    /// <summary>Absolute path of the per-run temp config home.</summary>
    public string TempHome { get; }

    /// <summary>Env var(s) to inject into the child so the CLI reads the temp home.</summary>
    public IReadOnlyDictionary<string, string> EnvOverrides { get; }

    /// <summary>Context sources describing the temp home + seeded files (read-only observability).</summary>
    public IReadOnlyList<CliContextSource> Sources { get; }

    /// <summary>Delete the per-run temp home. Idempotent; failures are logged, never thrown.</summary>
    public void Dispose()
    {
        if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try
        {
            if (Directory.Exists(TempHome))
                Directory.Delete(TempHome, recursive: true);
        }
        catch (Exception ex)
        {
            // A leaked temp home is a minor disk-hygiene issue, never a run failure:
            // the OS temp dir is reclaimed eventually and the next run gets its own
            // fresh Guid-suffixed home anyway.
            _logger?.LogDebug(ex, "Failed to clean up clean-context temp home {Path}", TempHome);
        }
    }
}

/// <summary>
/// Builds a CLI's <see cref="CleanContextPreparation"/>: creates the per-run temp
/// home, links refreshable credentials, copies isolated base config, and reports
/// the resulting paths. Side-effect-light (it only touches a brand-new temp dir
/// under <see cref="Path.GetTempPath"/>) so it is directly unit-testable with an
/// injected fake home.
/// </summary>
internal static class CleanContextPreparer
{
    /// <summary>
    /// Files linked from <c>~/.claude</c> into a clean <c>CLAUDE_CONFIG_DIR</c> so
    /// OAuth refreshes remain visible to the source home.
    /// </summary>
    private static readonly string[] ClaudeLinkedSeedFiles = [".credentials.json"];

    /// <summary>
    /// Files copied from <c>~/.claude</c> so clean-home settings changes remain
    /// isolated. The recipe excludes project transcripts, history, and user memory.
    /// </summary>
    private static readonly string[] ClaudeCopiedSeedFiles = ["settings.json"];

    /// <summary>Codex auth files linked so in-place token refreshes reach the source home.</summary>
    private static readonly string[] CodexLinkedSeedFiles = ["auth.json"];

    /// <summary>
    /// Codex config files copied into the clean home. The recipe excludes sessions
    /// and history.
    /// </summary>
    private static readonly string[] CodexCopiedSeedFiles = ["config.toml"];

    /// <summary>
    /// Build the Claude clean context (<c>CLAUDE_CONFIG_DIR</c> redirect).
    /// <paramref name="userHome"/> is the user profile root (USERPROFILE / HOME); the
    /// source config dir is <c>{userHome}/.claude</c>. Returns null only when the
    /// temp home cannot be created (clean is then impossible and the caller falls
    /// back to shared).
    /// </summary>
    public static CleanContextPreparation? PrepareClaude(string? userHome, ILogger? logger = null)
    {
        var source = string.IsNullOrWhiteSpace(userHome) ? null : Path.Combine(userHome, ".claude");
        return Prepare(
            CliTypes.Claude,
            "CLAUDE_CONFIG_DIR",
            source,
            ClaudeLinkedSeedFiles,
            ClaudeCopiedSeedFiles,
            logger);
    }

    /// <summary>
    /// Build the Codex clean context (<c>CODEX_HOME</c> redirect). The source config
    /// dir is <c>{userHome}/.codex</c>.
    /// </summary>
    public static CleanContextPreparation? PrepareCodex(string? userHome, ILogger? logger = null)
    {
        var source = string.IsNullOrWhiteSpace(userHome) ? null : Path.Combine(userHome, ".codex");
        return Prepare(
            CliTypes.Codex,
            "CODEX_HOME",
            source,
            CodexLinkedSeedFiles,
            CodexCopiedSeedFiles,
            logger);
    }

    /// <summary>
    /// Build a clean context from a descriptor's <see cref="CleanContextSpec"/> — the
    /// data-driven path the engine uses (the per-CLI recipe lives on the descriptor, the
    /// mechanics here). The source dir is <c>{userHome}/{spec.SourceConfigDirName}</c>.
    /// </summary>
    public static CleanContextPreparation? PrepareFromSpec(string cliType, CleanContextSpec spec, string? userHome, ILogger? logger = null)
    {
        var source = string.IsNullOrWhiteSpace(userHome) ? null : Path.Combine(userHome, spec.SourceConfigDirName);
        return Prepare(
            cliType,
            spec.EnvVar,
            source,
            spec.LinkedSeedFiles,
            spec.CopiedSeedFiles,
            logger);
    }

    private static CleanContextPreparation? Prepare(
        string cliType,
        string envVar,
        string? sourceDir,
        IReadOnlyList<string> linkedSeedFiles,
        IReadOnlyList<string> copiedSeedFiles,
        ILogger? logger)
    {
        string tempHome;
        try
        {
            tempHome = Path.Combine(
                Path.GetTempPath(),
                "coding-agent-runner-clean-context",
                $"{cliType}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempHome);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Could not create clean-context temp home for {Cli}; falling back to shared", cliType);
            return null;
        }

        var sources = new List<CliContextSource>
        {
            new()
            {
                Kind = CliContextSourceKinds.Env,
                Label = envVar,
                Path = tempHome,
                Exists = true,
                Detail = "isolated clean-context home seeded for this run",
            },
        };

        SeedFiles(linkedSeedFiles, linked: true);
        SeedFiles(copiedSeedFiles, linked: false);

        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [envVar] = tempHome };
        return new CleanContextPreparation(cliType, tempHome, env, sources, logger);

        void SeedFiles(IReadOnlyList<string> seedFiles, bool linked)
        {
            foreach (var rel in seedFiles)
            {
                if (string.IsNullOrWhiteSpace(sourceDir)) break;
                var src = Path.Combine(sourceDir, rel);
                var dst = Path.Combine(tempHome, rel);
                try
                {
                    if (!File.Exists(src)) continue;
                    var dstDir = Path.GetDirectoryName(dst);
                    if (!string.IsNullOrEmpty(dstDir)) Directory.CreateDirectory(dstDir);
                    var method = linked
                        ? LinkOrCopy(src, dst, logger)
                        : SeedFileMethod.Copy;
                    if (!linked) File.Copy(src, dst, overwrite: true);
                    sources.Add(new CliContextSource
                    {
                        Kind = CliContextSourceKinds.GlobalConfig,
                        Label = $"Seeded {rel}",
                        Path = dst,
                        Exists = true,
                        Detail = $"{Describe(method)} from {src}",
                    });
                }
                catch (Exception ex)
                {
                    // A failed seed is not fatal: auth may come from an env var
                    // (ANTHROPIC_API_KEY / Codex auth) instead of the file, so the
                    // clean run can still succeed. Note it for diagnostics only.
                    logger?.LogDebug(ex, "Could not seed {File} into clean {Cli} home", rel, cliType);
                }
            }
        }
    }

    internal static SeedFileMethod LinkOrCopy(
        string source,
        string destination,
        ILogger? logger = null,
        Action<string, string>? createHardLink = null,
        Action<string, string>? createSymbolicLink = null)
    {
        try
        {
            (createHardLink ?? CreateHardLink)(source, destination);
            return SeedFileMethod.HardLink;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(
                ex,
                "Clean-context hardlink unavailable for {Source}; trying a symbolic link",
                source);
        }

        try
        {
            (createSymbolicLink ?? CreateSymbolicLink)(source, destination);
            return SeedFileMethod.SymbolicLink;
        }
        catch (Exception ex)
        {
            logger?.LogDebug(
                ex,
                "Clean-context symbolic link unavailable for {Source}; copying the file",
                source);
        }

        File.Copy(source, destination, overwrite: true);
        return SeedFileMethod.Copy;
    }

    private static string Describe(SeedFileMethod method) => method switch
    {
        SeedFileMethod.HardLink => "hard-linked",
        SeedFileMethod.SymbolicLink => "symbolically linked",
        _ => "copied",
    };

    private static void CreateHardLink(string source, string destination)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkWindows(destination, source, IntPtr.Zero))
                throw new IOException(
                    $"Could not create hardlink '{destination}' to '{source}'.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
            return;
        }

        if (CreateHardLinkUnix(source, destination) != 0)
            throw new IOException(
                $"Could not create hardlink '{destination}' to '{source}'.",
                new Win32Exception(Marshal.GetLastWin32Error()));
    }

    private static void CreateSymbolicLink(string source, string destination) =>
        File.CreateSymbolicLink(destination, source);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int CreateHardLinkUnix(string existingFileName, string fileName);
}

internal enum SeedFileMethod
{
    HardLink,
    SymbolicLink,
    Copy,
}

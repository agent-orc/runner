using System.Text.RegularExpressions;

namespace CodingAgentRunner.Execution.Hardening;

/// <summary>
/// Resolves a coding-agent CLI name/path to the most direct executable to spawn,
/// filing off the Windows sharp edges that otherwise corrupt a run.
///
/// <para><b>The headline fix.</b> On Windows, npm installs a CLI as a tiny
/// <c>.cmd</c> batch shim (e.g. <c>claude.cmd</c>) with no bare <c>.exe</c> on
/// <c>PATH</c>. Launching the <c>.cmd</c> makes Windows run
/// <c>cmd.exe /c claude.cmd …</c>, and <c>cmd.exe</c> treats the newline inside a
/// multi-line <c>-p &lt;prompt&gt;</c> argument as a command separator — so the
/// agent receives only the FIRST LINE of the prompt and never the task. Resolving
/// the shim to the real <c>.exe</c> it invokes and launching that directly makes
/// <c>CreateProcess</c> parse the argument via <c>CommandLineToArgvW</c>, and the
/// multi-line prompt survives verbatim.</para>
/// </summary>
public static class BinaryResolver
{
    /// <summary>
    /// Resolve <paramref name="nameOrPath"/> to a launchable executable: walk
    /// <c>PATH</c>/<c>PATHEXT</c> on Windows, then unwrap a <c>.cmd</c>/<c>.bat</c>
    /// shim to the real <c>.exe</c> it calls. On non-Windows the name is returned
    /// unchanged (the shell resolves it).
    /// </summary>
    public static string Resolve(string nameOrPath)
        => ResolveShimToExe(ResolveExecutable(nameOrPath));

    /// <summary>
    /// Windows <c>PATH</c> + <c>PATHEXT</c> resolution: turn a bare command name
    /// into the first matching file on <c>PATH</c> (e.g. <c>claude</c> →
    /// <c>…\claude.cmd</c>). Returns the input unchanged on non-Windows or when
    /// nothing matches.
    /// </summary>
    public static string ResolveExecutable(string nameOrPath)
    {
        if (string.IsNullOrWhiteSpace(nameOrPath)) return nameOrPath;
        if (!OperatingSystem.IsWindows()) return nameOrPath;
        if (Path.IsPathRooted(nameOrPath) && File.Exists(nameOrPath)) return nameOrPath;
        if (Path.HasExtension(nameOrPath) && File.Exists(nameOrPath)) return nameOrPath;

        var exts = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
            .Split(';', StringSplitOptions.RemoveEmptyEntries);

        if (Path.IsPathRooted(nameOrPath))
        {
            foreach (var ext in exts)
            {
                var candidate = nameOrPath + ext;
                if (File.Exists(candidate)) return candidate;
            }
            return nameOrPath;
        }

        var dirs = (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in dirs)
        {
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, nameOrPath + ext);
                if (File.Exists(candidate)) return candidate;
            }
        }
        return nameOrPath;
    }

    private static readonly Regex ExeInShim =
        new("\"([^\"]*?\\.exe)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// If <paramref name="path"/> is a Windows <c>.cmd</c>/<c>.bat</c> shim that
    /// launches a real <c>.exe</c> (the npm convention
    /// <c>"%~dp0\…\tool.exe" %*</c>), return that <c>.exe</c>'s absolute path so
    /// the caller can bypass <c>cmd.exe</c>. Returns the input unchanged for a
    /// shim that launches a script (e.g. <c>node …</c>), on non-Windows, or when
    /// nothing can be resolved.
    /// </summary>
    public static string ResolveShimToExe(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;
        if (!OperatingSystem.IsWindows()) return path;
        if (!path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)
            && !path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            return path;
        if (!File.Exists(path)) return path;

        string content;
        try { content = File.ReadAllText(path); }
        catch { return path; }

        var match = ExeInShim.Match(content);
        if (!match.Success) return path;

        var dir = Path.GetDirectoryName(path) ?? string.Empty;

        // Resolve the npm shim's %~dp0 / %dp0% prefix (= the shim's own directory).
        var relative = match.Groups[1].Value
            .Replace("%~dp0", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("%dp0%", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("%dp0", string.Empty, StringComparison.OrdinalIgnoreCase)
            .TrimStart('\\', '/');

        string candidate;
        try { candidate = Path.GetFullPath(Path.Combine(dir, relative)); }
        catch { return path; }

        return File.Exists(candidate) ? candidate : path;
    }
}

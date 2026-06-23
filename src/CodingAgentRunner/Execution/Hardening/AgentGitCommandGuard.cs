using System.Diagnostics;
using CodingAgentRunner.Abstractions;
using Microsoft.Extensions.Logging;

namespace CodingAgentRunner.Execution.Hardening;

/// <summary>
/// Enforces the <em>host owns version control</em> boundary: the worker agent must
/// not run mutating git commands (<c>commit</c>, <c>push</c>, …); the host commits
/// and pushes. It works by prepending a tiny <c>git</c> wrapper to the spawned
/// process's <c>PATH</c> that blocks the forbidden subcommands and execs the real
/// git for everything else.
///
/// <para><b>Windows note.</b> The agent invokes <c>git</c> through its git-bash
/// shell, which resolves a bare <c>git</c> to an <em>extensionless</em> script,
/// NOT a <c>.cmd</c> (only <c>cmd.exe</c> uses <c>PATHEXT</c>). So both an
/// extensionless <c>git</c> (for git-bash) and a <c>git.cmd</c> (for cmd.exe) are
/// written — a <c>.cmd</c>-only guard is silently bypassed.</para>
/// </summary>
internal static class AgentGitCommandGuard
{
    private static readonly string[] GlobalOptionsWithValue =
        ["-C", "-c", "--git-dir", "--work-tree", "--namespace", "--exec-path"];

    /// <summary>
    /// Inject the guard into <paramref name="psi"/> (a PATH-front wrapper + the
    /// real-git / guard-dir environment). No-op when <paramref name="allowMutation"/>
    /// is set, when the allow-env is already <c>1</c>, or when no real git is found.
    /// </summary>
    public static void Apply(ProcessStartInfo psi, GitGuardOptions options,
                             bool allowMutation = false, ILogger? logger = null)
    {
        if (allowMutation) return;

        var allowEnv = AllowEnvName(options);
        if (psi.Environment.TryGetValue(allowEnv, out var v)
            && string.Equals(v, "1", StringComparison.OrdinalIgnoreCase))
            return;

        var realGit = ResolveRealGitExecutable();
        if (string.IsNullOrWhiteSpace(realGit)) return;

        var guardDir = EnsureGuardDirectory(options, logger);
        if (string.IsNullOrWhiteSpace(guardDir)) return;

        var existingPath = psi.Environment.TryGetValue("PATH", out var p)
            ? p
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        psi.Environment[RealGitEnvName(options)] = realGit;
        psi.Environment[GuardDirEnvName(options)] = guardDir;
        psi.Environment["PATH"] = guardDir + Path.PathSeparator + existingPath;
    }

    /// <summary>Whether <paramref name="args"/> name a forbidden git subcommand (past global options).</summary>
    public static bool IsForbidden(IReadOnlyList<string> args, GitGuardOptions options)
    {
        var cmd = ResolveGitCommand(args);
        return cmd is not null
               && options.ForbiddenCommands.Any(c => string.Equals(c, cmd, StringComparison.OrdinalIgnoreCase));
    }

    private static string AllowEnvName(GitGuardOptions o) => $"{o.EnvPrefix}_ALLOW_GIT_MUTATION";
    private static string RealGitEnvName(GitGuardOptions o) => $"{o.EnvPrefix}_REAL_GIT";
    private static string GuardDirEnvName(GitGuardOptions o) => $"{o.EnvPrefix}_GUARD_DIR";

    private static string? ResolveGitCommand(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (string.IsNullOrWhiteSpace(arg)) continue;
            if (GlobalOptionsWithValue.Any(o => string.Equals(o, arg, StringComparison.OrdinalIgnoreCase))) { i++; continue; }
            if (GlobalOptionsWithValue.Any(o => arg.StartsWith(o + "=", StringComparison.OrdinalIgnoreCase))) continue;
            if (arg.StartsWith('-')) continue;
            return arg;
        }
        return null;
    }

    private static string? ResolveRealGitExecutable()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var exts = OperatingSystem.IsWindows()
            ? (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD")
                .Split(';', StringSplitOptions.RemoveEmptyEntries)
            : [string.Empty];

        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in exts)
            {
                var candidate = Path.Combine(dir, "git" + ext);
                if (File.Exists(candidate)) return candidate;
            }

        if (OperatingSystem.IsWindows())
        {
            string[] common =
            [
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "cmd", "git.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "cmd", "git.exe")
            ];
            foreach (var c in common) if (File.Exists(c)) return c;
        }
        return null;
    }

    private static string? EnsureGuardDirectory(GitGuardOptions options, ILogger? logger)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), options.GuardDirName);
            Directory.CreateDirectory(dir);
            var marker = AllowEnvName(options);

            // Extensionless `git` — the one git-bash actually resolves.
            var shPath = Path.Combine(dir, "git");
            if (!File.Exists(shPath) || !File.ReadAllText(shPath).Contains(marker, StringComparison.Ordinal))
            {
                File.WriteAllText(shPath, BuildPosixWrapper(options).Replace("\r\n", "\n"));
                TryMarkExecutable(shPath);
            }

            // `git.cmd` as well on Windows, for any cmd.exe-resolved caller.
            if (OperatingSystem.IsWindows())
            {
                var cmdPath = Path.Combine(dir, "git.cmd");
                if (!File.Exists(cmdPath) || !File.ReadAllText(cmdPath).Contains(marker, StringComparison.Ordinal))
                    File.WriteAllText(cmdPath, BuildWindowsWrapper(options));
            }

            return dir;
        }
        catch (Exception ex)
        {
            // A silent failure is dangerous: the guard is the only thing enforcing
            // the boundary, so surface it rather than swallow.
            logger?.LogWarning(ex, "git guard: could not prepare the guard directory; the guard is NOT active for this run");
            return null;
        }
    }

    private static string BuildPosixWrapper(GitGuardOptions o) => PosixTemplate
        .Replace("__ALLOW__", AllowEnvName(o))
        .Replace("__REALGIT__", RealGitEnvName(o))
        .Replace("__FORBIDDEN__", string.Join("|", o.ForbiddenCommands))
        .Replace("__MSG__", o.BlockMessage.Replace("{cmd}", "$cmd"));

    private static string BuildWindowsWrapper(GitGuardOptions o)
    {
        var gotos = string.Join("\r\n",
            o.ForbiddenCommands.Select(c => $"if /I \"%_cmd%\"==\"{c}\" goto block"));
        return WindowsTemplate
            .Replace("__ALLOW__", AllowEnvName(o))
            .Replace("__REALGIT__", RealGitEnvName(o))
            .Replace("__FORBIDDEN_GOTOS__", gotos)
            .Replace("__MSG__", o.BlockMessage.Replace("{cmd}", "%_cmd%"));
    }

    private static void TryMarkExecutable(string path)
    {
        // Unix mode bits only mean something on POSIX hosts; the chmod is a no-op
        // concept on Windows (and File.SetUnixFileMode is unsupported there).
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch { /* best-effort; some filesystems ignore unix mode bits */ }
    }

    private const string PosixTemplate = """
#!/bin/sh
cmd=""
skip=""
for arg in "$@"; do
  if [ -n "$skip" ]; then skip=""; continue; fi
  case "$arg" in
    -C|-c|--git-dir|--work-tree|--namespace|--exec-path) skip=1; continue ;;
    --git-dir=*|--work-tree=*|--namespace=*|--exec-path=*) continue ;;
    -*) continue ;;
    *) cmd="$arg"; break ;;
  esac
done
if [ "${__ALLOW__}" != "1" ]; then
  case "$cmd" in
    __FORBIDDEN__)
      echo "__MSG__" >&2
      exit 86
      ;;
  esac
fi
exec "${__REALGIT__}" "$@"
""";

    private const string WindowsTemplate = """
@echo off
setlocal
set "_cmd="
set "_skip="
for %%A in (%*) do (
  if defined _skip (
    set "_skip="
  ) else if /I "%%~A"=="-C" (
    set "_skip=1"
  ) else if /I "%%~A"=="-c" (
    set "_skip=1"
  ) else if /I "%%~A"=="--git-dir" (
    set "_skip=1"
  ) else if /I "%%~A"=="--work-tree" (
    set "_skip=1"
  ) else if /I "%%~A"=="--namespace" (
    set "_skip=1"
  ) else if /I "%%~A"=="--exec-path" (
    set "_skip=1"
  ) else if not defined _cmd (
    echo %%~A | findstr /B /C:"-" >nul
    if errorlevel 1 set "_cmd=%%~A"
  )
)
if /I "%__ALLOW__%"=="1" goto run
__FORBIDDEN_GOTOS__
goto run
:block
echo __MSG__ 1>&2
exit /b 86
:run
"%__REALGIT__%" %*
exit /b %ERRORLEVEL%
""";
}

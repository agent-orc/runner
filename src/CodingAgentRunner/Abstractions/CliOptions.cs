namespace CodingAgentRunner.Abstractions;

/// <summary>
/// Consumer-supplied configuration for the runner — replaces the host
/// application's ambient configuration. Everything is optional with sane
/// defaults; a CLI name on <c>PATH</c> is used when no explicit path is given.
/// </summary>
public sealed record CliOptions
{
    /// <summary>Explicit path/command for the Claude Code CLI (default: <c>claude</c> on PATH).</summary>
    public string? ClaudePath { get; init; }

    /// <summary>Explicit path/command for the Codex CLI (default: <c>codex</c> on PATH).</summary>
    public string? CodexPath { get; init; }

    /// <summary>Explicit path/command for the Gemini CLI. <b>Deprecated</b> — Gemini support is unmaintained and removal is planned before 1.0.</summary>
    [Obsolete("Gemini CLI support is deprecated and unmaintained; removal is planned before 1.0. Use Antigravity (agentapi) for Google models.")]
    public string? GeminiPath { get; init; }

    /// <summary>Explicit path/command for the Antigravity CLI (default: <c>agentapi</c> on PATH).</summary>
    public string? AntigravityPath { get; init; }

    /// <summary>Extra environment variables applied to every spawned CLI process.</summary>
    public IReadOnlyDictionary<string, string>? EnvironmentOverrides { get; init; }

    /// <summary>When <c>true</c>, the git guard is disabled (the agent may run mutating git).</summary>
    public bool AllowAgentGitMutation { get; init; }

    /// <summary>Git-guard configuration.</summary>
    public GitGuardOptions GitGuard { get; init; } = new();

    /// <summary>Process-hardening configuration.</summary>
    public CliHardeningOptions Hardening { get; init; } = new();

    /// <summary>
    /// Optional custom process spawner — inject one (e.g. a Windows pseudo-terminal
    /// spawner) to change how the engine launches a CLI. Null uses plain redirected
    /// pipes. See <see cref="ICliProcessSpawner"/>.
    /// </summary>
    public ICliProcessSpawner? Spawner { get; init; }
}

/// <summary>
/// Parameterizes the git guard so it carries no host-application branding. The
/// guard injects a PATH-front <c>git</c> wrapper that blocks mutating commands
/// unless the allow-env (derived from <see cref="EnvPrefix"/>) is set.
/// </summary>
public sealed record GitGuardOptions
{
    /// <summary>Prefix for the guard's environment variables (allow / real-git / guard-dir).</summary>
    public string EnvPrefix { get; init; } = "CODING_AGENT_RUNNER";

    /// <summary>Name of the temp directory that holds the generated wrappers.</summary>
    public string GuardDirName { get; init; } = "coding-agent-runner-git-guard";

    /// <summary>Git subcommands the worker agent is not allowed to run.</summary>
    public IReadOnlyList<string> ForbiddenCommands { get; init; } =
    [
        "commit", "push", "commit-tree", "tag", "reset",
        "checkout", "switch", "branch", "restore", "clean", "stash", "notes"
    ];

    /// <summary>Message printed to stderr when a forbidden command is blocked. <c>{cmd}</c> is substituted.</summary>
    public string BlockMessage { get; init; } =
        "coding-agent-runner git guard: worker agents must not run git {cmd}; the host owns commit and push.";
}

/// <summary>Process-hardening toggles applied at spawn time.</summary>
public sealed record CliHardeningOptions
{
    /// <summary>Deny stdin by default: on Windows a connected stdin pipe can race CLI init and wedge the process.</summary>
    public bool DenyStdin { get; init; } = true;

    /// <summary>Force UTF-8 stdio + locale so non-ASCII output does not corrupt or crash the CLI.</summary>
    public bool EnforceUtf8 { get; init; } = true;
}

using CodingAgentRunner.Model;

namespace CodingAgentRunner.Diagnostics;

/// <summary>
/// How to install and sign in to one supported CLI — the static setup knowledge
/// the library ships, so a host application can tell its user (or a provisioning
/// script) what is missing and which commands fix it. Pure data; probing the
/// live machine is <see cref="CliRunner.InspectEnvironment"/>.
/// </summary>
public sealed record CliSetupInfo
{
    /// <summary>One of <see cref="CliTypes"/>.</summary>
    public required string CliType { get; init; }

    /// <summary>Human-readable product name (e.g. "Claude Code").</summary>
    public required string DisplayName { get; init; }

    /// <summary>The command probed on <c>PATH</c> (e.g. <c>claude</c>, <c>agentapi</c>).</summary>
    public required string Command { get; init; }

    /// <summary>The npm package that installs the CLI, or null when it is not npm-distributed.</summary>
    public string? NpmPackage { get; init; }

    /// <summary>
    /// Shell commands that install the CLI, most recommended first. Each entry is
    /// directly scriptable (CI, provisioning) except where
    /// <see cref="AutomationNotes"/> says otherwise.
    /// </summary>
    public required IReadOnlyList<string> InstallCommands { get; init; }

    /// <summary>
    /// The sign-in procedure, as ordered human-readable steps. Interactive OAuth
    /// flows cannot run headless; <see cref="ApiKeyEnvVar"/> and
    /// <see cref="CredentialFiles"/> describe the non-interactive alternatives.
    /// </summary>
    public required IReadOnlyList<string> LoginSteps { get; init; }

    /// <summary>Environment variable that authenticates without an interactive login, or null when none exists.</summary>
    public string? ApiKeyEnvVar { get; init; }

    /// <summary>
    /// Credential files the CLI writes after a sign-in, relative to the user home
    /// (e.g. <c>.claude/.credentials.json</c>). Their presence is the signal
    /// <see cref="CliRunner.InspectEnvironment"/> uses; copying them to another
    /// machine is the non-interactive way to seed a login.
    /// </summary>
    public IReadOnlyList<string> CredentialFiles { get; init; } = [];

    /// <summary>Official setup documentation.</summary>
    public required string DocsUrl { get; init; }

    /// <summary>Caveats for scripted installs / headless auth (e.g. "login is interactive-only").</summary>
    public string? AutomationNotes { get; init; }

    /// <summary>
    /// One install command for the current platform: the npm command when the CLI
    /// is npm-distributed (works everywhere), otherwise the platform-matching
    /// native installer from <see cref="InstallCommands"/>.
    /// </summary>
    public string RecommendedInstallCommand
    {
        get
        {
            if (NpmPackage is not null) return $"npm install -g {NpmPackage}";
            var marker = OperatingSystem.IsWindows() ? "install.ps1" : "install.sh";
            return InstallCommands.FirstOrDefault(c => c.Contains(marker, StringComparison.OrdinalIgnoreCase))
                   ?? InstallCommands[0];
        }
    }
}

/// <summary>
/// The setup catalog: one <see cref="CliSetupInfo"/> per supported CLI. Static
/// data, current as of this library version — the CLIs evolve, so treat the
/// commands as the default answer, not a guarantee, and prefer
/// <see cref="CliSetupInfo.DocsUrl"/> when they fail.
/// </summary>
public static class CliSetup
{
    /// <summary>Claude Code (Anthropic).</summary>
    public static readonly CliSetupInfo Claude = new()
    {
        CliType = CliTypes.Claude,
        DisplayName = "Claude Code",
        Command = "claude",
        NpmPackage = "@anthropic-ai/claude-code",
        InstallCommands =
        [
            "irm https://claude.ai/install.ps1 | iex   # Windows native installer",
            "curl -fsSL https://claude.ai/install.sh | bash   # macOS/Linux native installer",
            "npm install -g @anthropic-ai/claude-code",
            "winget install Anthropic.ClaudeCode",
        ],
        LoginSteps =
        [
            "Run `claude` in a terminal; the first run opens a browser sign-in (Claude subscription or Anthropic Console account).",
            "Non-interactive alternative: run `claude setup-token` once on a machine with a browser, then set the printed long-lived token as CLAUDE_CODE_OAUTH_TOKEN — or set ANTHROPIC_API_KEY (API billing).",
        ],
        ApiKeyEnvVar = "ANTHROPIC_API_KEY",
        CredentialFiles = [".claude/.credentials.json"],
        DocsUrl = "https://code.claude.com/docs/en/setup",
        AutomationNotes = "Install is scriptable (native installer, npm, winget). The OAuth login is interactive; for headless machines set CLAUDE_CODE_OAUTH_TOKEN (from `claude setup-token`) or ANTHROPIC_API_KEY, or copy .claude/.credentials.json from a signed-in machine. On macOS credentials live in the Keychain, not the file.",
    };

    /// <summary>OpenAI Codex CLI.</summary>
    public static readonly CliSetupInfo Codex = new()
    {
        CliType = CliTypes.Codex,
        DisplayName = "OpenAI Codex CLI",
        Command = "codex",
        NpmPackage = "@openai/codex",
        InstallCommands =
        [
            "npm install -g @openai/codex",
            "irm https://chatgpt.com/codex/install.ps1 | iex   # Windows native installer",
            "curl -fsSL https://chatgpt.com/codex/install.sh | sh   # macOS/Linux native installer",
            "brew install --cask codex   # macOS",
        ],
        LoginSteps =
        [
            "Run `codex login`; it opens a browser sign-in with your ChatGPT account (Plus/Pro/Business/Enterprise).",
            "Non-interactive alternatives: `codex login --device-auth` (device-code flow), or pipe an OpenAI API key into `codex login --with-api-key` (usage-based billing instead of the subscription).",
        ],
        ApiKeyEnvVar = "OPENAI_API_KEY",
        CredentialFiles = [".codex/auth.json"],
        DocsUrl = "https://developers.openai.com/codex/cli",
        AutomationNotes = "Install is scriptable. The ChatGPT login is interactive; for headless machines use `codex login --device-auth`, `codex login --with-api-key`, or copy .codex/auth.json from a signed-in machine (officially documented).",
    };

    /// <summary>Google Gemini CLI (deprecated in this library; superseded by Antigravity).</summary>
    public static readonly CliSetupInfo Gemini = new()
    {
#pragma warning disable CS0618 // setup/diagnostics keep reporting the deprecated CLI until its pre-1.0 removal
        CliType = CliTypes.Gemini,
#pragma warning restore CS0618
        DisplayName = "Google Gemini CLI",
        Command = "gemini",
        NpmPackage = "@google/gemini-cli",
        InstallCommands =
        [
            "npm install -g @google/gemini-cli",
        ],
        LoginSteps =
        [
            "Run `gemini` in a terminal; the first run offers a Google-account browser sign-in.",
            "Non-interactive alternative: set GEMINI_API_KEY (Google AI Studio key), or a Vertex service account via GOOGLE_APPLICATION_CREDENTIALS.",
        ],
        ApiKeyEnvVar = "GEMINI_API_KEY",
        CredentialFiles = [".gemini/oauth_creds.json"],
        DocsUrl = "https://geminicli.com/docs/get-started/",
        AutomationNotes = "Install is scriptable. OAuth login is interactive; for headless machines use GEMINI_API_KEY or a service account, or reuse a cached .gemini/oauth_creds.json. Deprecated in this library — prefer Antigravity for new Google integration.",
    };

    /// <summary>Google Antigravity's <c>agentapi</c> CLI.</summary>
    public static readonly CliSetupInfo Antigravity = new()
    {
        CliType = CliTypes.Antigravity,
        DisplayName = "Google Antigravity (agentapi)",
        Command = "agentapi",
        NpmPackage = null,
        InstallCommands =
        [
            "irm https://antigravity.google/cli/install.ps1 | iex   # Windows: installs the Antigravity CLI (`agy`), which provides `agentapi`",
            "curl -fsSL https://antigravity.google/cli/install.sh | bash   # macOS/Linux",
        ],
        LoginSteps =
        [
            "Run `agy` once; it opens a Google-account browser sign-in (on SSH/headless it prints a URL + one-time code). `agentapi` reuses that session.",
        ],
        ApiKeyEnvVar = null,
        CredentialFiles = [],
        DocsUrl = "https://antigravity.google/docs/cli-overview",
        AutomationNotes = "Install is scriptable. Credentials live in the OS keyring (no file to probe or copy), and no headless token export is documented — the first sign-in per machine needs the interactive URL + code flow.",
    };

    /// <summary>Every supported CLI's setup info, in catalog order.</summary>
    public static readonly IReadOnlyList<CliSetupInfo> All = [Claude, Codex, Gemini, Antigravity];

    /// <summary>
    /// Resolve setup info by CLI type (case-insensitive). Throws
    /// <see cref="ArgumentException"/> for an unknown type — no silent fallback.
    /// </summary>
    public static CliSetupInfo For(string cliType)
        => All.FirstOrDefault(s => string.Equals(s.CliType, cliType?.Trim(), StringComparison.OrdinalIgnoreCase))
           ?? throw new ArgumentException($"No setup info for CLI type '{cliType}'. Known: {string.Join(", ", All.Select(s => s.CliType))}.", nameof(cliType));
}

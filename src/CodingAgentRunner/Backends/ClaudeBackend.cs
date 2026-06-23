using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Execution.Hardening;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Backends;

/// <summary>
/// Claude Code backend. Invokes <c>claude -p &lt;prompt&gt; --output-format
/// stream-json --verbose</c>, maps the NDJSON frames via
/// <see cref="ClaudeEventAdapter"/>, and isolates clean runs through
/// <c>CLAUDE_CONFIG_DIR</c>.
///
/// <para>
/// The prompt is the LAST positional argv (not piped through stdin), and the
/// binary is resolved to the real <c>claude.exe</c> rather than the npm
/// <c>.cmd</c> shim — on Windows, routing a multi-line <c>-p</c> prompt through
/// <c>cmd.exe</c> truncates it at the first newline, so the agent would see only
/// the first line of its brief.
/// </para>
/// </summary>
public sealed class ClaudeBackend : CliBackendBase
{
    /// <summary>Create a Claude backend.</summary>
    public ClaudeBackend(
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null)
        : base(options, logger, logPaths, home) { }

    /// <inheritdoc />
    public override string CliType => CliTypes.Claude;

    /// <inheritdoc />
    public override string GetCliPath() => Options.ClaudePath ?? "claude";

    /// <inheritdoc />
    public override bool SupportsCleanContext => true;

    /// <inheritdoc />
    public override CleanContextPreparation? PrepareCleanContext(string workingDirectory)
        => CleanContextPreparer.PrepareClaude(Home.GetUserHome(), Logger);

    /// <inheritdoc />
    protected override ProcessStartInfo BuildStartInfo(CliRunRequest request, string? model, string? thinkingLevel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveClaudeBinary(GetCliPath()),
            WorkingDirectory = request.WorkingDirectory,
        };

        // ArgumentList escapes each arg per CommandLineToArgvW — the only correct
        // path for a multi-line / quote-rich prompt.
        psi.ArgumentList.Add("-p");

        if (request.ResumeSession && !string.IsNullOrWhiteSpace(request.SessionName))
        {
            psi.ArgumentList.Add("-r");
            psi.ArgumentList.Add(request.SessionName!);
        }

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model!);
        }

        foreach (var flag in CliReasoningFlags.For(CliType, model, thinkingLevel))
            psi.ArgumentList.Add(flag);

        psi.ArgumentList.Add("--output-format");
        psi.ArgumentList.Add("stream-json");
        psi.ArgumentList.Add("--verbose");

        foreach (var flag in CliPermissionFlags.For(CliType, request.PermissionMode))
            psi.ArgumentList.Add(flag);

        if (!string.IsNullOrWhiteSpace(request.SystemPromptFile) && File.Exists(request.SystemPromptFile))
        {
            psi.ArgumentList.Add("--append-system-prompt-file");
            psi.ArgumentList.Add(request.SystemPromptFile!);
        }

        // The prompt is the LAST positional argument.
        if (!string.IsNullOrEmpty(request.Prompt))
            psi.ArgumentList.Add(request.Prompt);

        return psi;
    }

    /// <summary>Claude takes the prompt as argv, never via stdin (avoids the stdin-pipe init race).</summary>
    protected override string? GetPromptStdinPayload(CliRunRequest request, string? model) => null;

    /// <inheritdoc />
    protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string runId, CliOutputLine line)
        => line.Stream == "stdout" ? ClaudeEventAdapter.Map(line.Text, runId) : Array.Empty<CliRunEvent>();

    /// <summary>
    /// Pre-spawn self-heal: if the CLI does not answer <c>--version</c>, run the
    /// npm-shim healer and re-probe before failing the spawn.
    /// </summary>
    protected override async Task<(bool Ok, string? Error)> EnsureCliHealthyAsync(CancellationToken ct)
    {
        var probe = TestCliPath();
        if (probe.Available) return (true, null);

        Logger.LogWarning("claude --version failed pre-spawn at '{Path}'; running the npm-shim healer", probe.Path);
        var outcome = await NpmShimHealer.TryHealClaudeAsync(Logger, ct).ConfigureAwait(false);
        if (outcome.Actions.Count > 0)
            Logger.LogInformation("npm-shim healer actions for claude: {Actions}", string.Join("; ", outcome.Actions));
        if (!outcome.Available)
            return (false, outcome.Error ?? "npm-shim healer reported claude unavailable after repair");

        var verify = TestCliPath();
        return verify.Available ? (true, null) : (false, $"claude --version still failing after heal at '{verify.Path}'");
    }

    /// <summary>
    /// Resolve the configured command to the real <c>claude.exe</c>, rewriting an
    /// npm <c>.cmd</c> shim to the bundled exe it calls. Spawning the <c>.cmd</c>
    /// with a multi-line argv on Windows truncates the prompt at the first newline.
    /// </summary>
    private string ResolveClaudeBinary(string nameOrPath)
    {
        string resolved;
        try { resolved = BinaryResolver.ResolveExecutable(nameOrPath); }
        catch { return nameOrPath; }

        try
        {
            var probed = BinaryResolver.ResolveShimToExe(resolved);
            if (!string.Equals(probed, resolved, StringComparison.OrdinalIgnoreCase))
            {
                Logger.LogInformation("[claude-bin] Rewrote .cmd shim {Shim} -> bundled exe {Exe}", resolved, probed);
                return probed;
            }
        }
        catch (Exception ex) { Logger.LogDebug(ex, "[claude-bin] shim->exe probe failed; using resolved path {Path}", resolved); }

        return resolved;
    }
}

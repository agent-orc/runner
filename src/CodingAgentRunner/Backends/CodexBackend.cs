using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Execution.Hardening;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Backends;

/// <summary>
/// Codex backend. Invokes <c>codex exec --experimental-json</c>, maps the JSONL
/// frames via <see cref="CodexEventAdapter"/>, and isolates clean runs through
/// <c>CODEX_HOME</c>.
///
/// <para>
/// The prompt is piped through stdin (the <c>-</c> argument) rather than passed as
/// a positional: a rules-heavy positional prompt is interpreted by Codex as
/// "initial instructions" and the model answers with a no-op. Reading from stdin
/// restores the user-message path. Completion is the App-Server protocol's
/// <c>turn.completed</c> frame plus the process exit — never a model-authored
/// sentinel.
/// </para>
/// </summary>
public sealed class CodexBackend : CliBackendBase
{
    // Codex `exec resume` only accepts a session UUID; a slug from another CLI
    // would make it error out, so we gate resume on this shape.
    private static readonly Regex CodexUuid =
        new(@"^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$", RegexOptions.Compiled);

    /// <summary>Create a Codex backend.</summary>
    public CodexBackend(
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null)
        : base(options, logger, logPaths, home) { }

    /// <inheritdoc />
    public override string CliType => CliTypes.Codex;

    /// <inheritdoc />
    public override string GetCliPath() => Options.CodexPath ?? "codex";

    /// <inheritdoc />
    public override bool SupportsCleanContext => true;

    /// <inheritdoc />
    public override CleanContextPreparation? PrepareCleanContext(string workingDirectory)
        => CleanContextPreparer.PrepareCodex(Home.GetUserHome(), Logger);

    /// <inheritdoc />
    protected override ProcessStartInfo BuildStartInfo(CliRunRequest request, string? model, string? thinkingLevel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SafeResolve(GetCliPath()),
            WorkingDirectory = request.WorkingDirectory,
        };
        psi.ArgumentList.Add("exec");

        // ALL exec-level options must precede the `resume` subcommand: --sandbox is
        // not a global flag, so `exec resume <id> --sandbox ...` errors out.
        psi.ArgumentList.Add("--experimental-json");

        foreach (var flag in CliPermissionFlags.For(CliType, request.PermissionMode))
            psi.ArgumentList.Add(flag);

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(model!);
        }

        foreach (var flag in CliReasoningFlags.For(CliType, model, thinkingLevel))
            psi.ArgumentList.Add(flag);

        var resume = request.ResumeSession
                     && !string.IsNullOrWhiteSpace(request.SessionName)
                     && CodexUuid.IsMatch(request.SessionName!);
        if (resume)
        {
            psi.ArgumentList.Add("resume");
            psi.ArgumentList.Add(request.SessionName!);
        }

        // `-` tells Codex to read the prompt from stdin (see GetPromptStdinPayload).
        if (!string.IsNullOrEmpty(request.Prompt))
            psi.ArgumentList.Add("-");

        return psi;
    }

    /// <summary>Codex reads the prompt from stdin to avoid the positional no-op.</summary>
    protected override string? GetPromptStdinPayload(CliRunRequest request, string? model)
        => string.IsNullOrEmpty(request.Prompt) ? null : request.Prompt;

    /// <inheritdoc />
    protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string runId, CliOutputLine line)
        => line.Stream == "stdout" ? CodexEventAdapter.Map(line.Text, runId) : Array.Empty<CliRunEvent>();

    private string SafeResolve(string path)
    {
        try { return BinaryResolver.ResolveExecutable(path); }
        catch { return path; }
    }
}

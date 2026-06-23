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
/// Gemini backend. Invokes <c>gemini -o stream-json</c> and maps the NDJSON frames
/// via <see cref="GeminiEventAdapter"/>. The folder-trust prompt is always
/// dismissed (<c>--skip-trust</c>) so an unattended run never blocks on it.
///
/// <para>Gemini exposes no config-home redirect, so it is shared-only
/// (<c>SupportsCleanContext</c> is false).</para>
/// </summary>
public sealed class GeminiBackend : CliBackendBase
{
    /// <summary>Create a Gemini backend.</summary>
    public GeminiBackend(
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null)
        : base(options, logger, logPaths, home) { }

    /// <inheritdoc />
    public override string CliType => CliTypes.Gemini;

    /// <inheritdoc />
    public override string GetCliPath() => Options.GeminiPath ?? "gemini";

    /// <inheritdoc />
    protected override ProcessStartInfo BuildStartInfo(CliRunRequest request, string? model, string? thinkingLevel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SafeResolve(GetCliPath()),
            WorkingDirectory = request.WorkingDirectory,
        };

        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add("stream-json");

        // --skip-trust (+ the approval flags) so no interactive prompt can hang.
        foreach (var flag in CliPermissionFlags.For(CliType, request.PermissionMode))
            psi.ArgumentList.Add(flag);

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(model!);
        }

        if (!string.IsNullOrEmpty(request.Prompt))
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(request.Prompt);
        }

        return psi;
    }

    /// <inheritdoc />
    protected override IEnumerable<CliRunEvent> MapLineToRunEvents(string runId, CliOutputLine line)
        => line.Stream == "stdout" ? GeminiEventAdapter.Map(line.Text, runId) : Array.Empty<CliRunEvent>();

    private string SafeResolve(string path)
    {
        try { return BinaryResolver.ResolveExecutable(path); }
        catch { return path; }
    }
}

using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Execution.Hardening;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Backends;

/// <summary>
/// GitHub Copilot CLI backend. Invokes <c>copilot -p &lt;prompt&gt; --allow-all</c>
/// headlessly.
///
/// <para>
/// Copilot has no documented stream-json mode, so this backend has no frame
/// adapter: it raises <see cref="Events.CliRunEvent.RunStarted"/> /
/// <see cref="Events.CliRunEvent.ProcessExited"/> and streams raw output lines,
/// but not the rich typed events the other CLIs emit. Copilot also block-buffers
/// stdout behind a pipe on Windows; a pseudo-terminal spawn (a future addition)
/// is needed for live line-by-line output there. Shared-only (no config-home
/// redirect).
/// </para>
/// </summary>
public sealed class CopilotBackend : CliBackendBase
{
    /// <summary>Create a Copilot backend.</summary>
    public CopilotBackend(
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null)
        : base(options, logger, logPaths, home) { }

    /// <inheritdoc />
    public override string CliType => CliTypes.Copilot;

    /// <inheritdoc />
    public override string GetCliPath() => Options.CopilotPath ?? "copilot";

    /// <inheritdoc />
    protected override ProcessStartInfo BuildStartInfo(CliRunRequest request, string? model, string? thinkingLevel)
    {
        var psi = new ProcessStartInfo
        {
            FileName = SafeResolve(GetCliPath()),
            WorkingDirectory = request.WorkingDirectory,
        };

        psi.ArgumentList.Add("-p");
        if (!string.IsNullOrEmpty(request.Prompt))
            psi.ArgumentList.Add(request.Prompt);

        // --allow-all is Copilot's only headless permission flag (YOLO).
        foreach (var flag in CliPermissionFlags.For(CliType, request.PermissionMode))
            psi.ArgumentList.Add(flag);

        if (!string.IsNullOrWhiteSpace(model))
        {
            psi.ArgumentList.Add("--model");
            psi.ArgumentList.Add(model!);
        }

        return psi;
    }

    private string SafeResolve(string path)
    {
        try { return BinaryResolver.ResolveExecutable(path); }
        catch { return path; }
    }
}

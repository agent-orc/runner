using Microsoft.Extensions.Logging;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Backends;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;

namespace CodingAgentRunner;

/// <summary>
/// The entry point: builds and holds one <see cref="ICliBackend"/> per supported
/// CLI from a single set of options, so a consumer wires the library once and then
/// resolves a backend by CLI type.
///
/// <code>
/// var runner = new CliRunner(new CliOptions());
/// var backend = runner.Get("claude");
/// backend.OnRunEvent += (runId, evt) => /* drive a watchdog / UI */;
/// var (run, error) = await backend.StartAsync(new CliRunRequest { ... });
/// </code>
/// </summary>
public sealed class CliRunner
{
    private readonly Dictionary<string, ICliBackend> _backends;

    /// <summary>Build a runner with backends for every supported CLI sharing the given options/providers.</summary>
    public CliRunner(
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null)
    {
        _backends = new Dictionary<string, ICliBackend>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude]  = new ClaudeBackend(options, logger, logPaths, home),
            [CliTypes.Codex]   = new CodexBackend(options, logger, logPaths, home),
            [CliTypes.Gemini]  = new GeminiBackend(options, logger, logPaths, home),
            [CliTypes.Copilot] = new CopilotBackend(options, logger, logPaths, home),
        };
    }

    /// <summary>The backends, one per supported CLI.</summary>
    public IReadOnlyCollection<ICliBackend> Backends => _backends.Values;

    /// <summary>The CLI types this runner can resolve.</summary>
    public IReadOnlyCollection<string> SupportedCliTypes => _backends.Keys;

    /// <summary>Resolve the backend for <paramref name="cliType"/> (normalized). Throws when unknown.</summary>
    public ICliBackend Get(string cliType)
        => TryGet(cliType, out var backend)
            ? backend
            : throw new ArgumentException($"No backend for CLI type '{cliType}'.", nameof(cliType));

    /// <summary>Try to resolve the backend for <paramref name="cliType"/> (normalized).</summary>
    public bool TryGet(string cliType, out ICliBackend backend)
        => _backends.TryGetValue(Model.CliTypes.Normalize(cliType), out backend!);
}

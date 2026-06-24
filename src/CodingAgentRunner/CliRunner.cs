using Microsoft.Extensions.Logging;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Drivers;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;

namespace CodingAgentRunner;

/// <summary>
/// The entry point: builds and holds one <see cref="ICliDriver"/> per supported
/// CLI from a single set of options, so a consumer wires the library once and then
/// resolves a driver by CLI type.
///
/// <code>
/// var runner = new CliRunner(new CliOptions());
/// var driver = runner.Get("claude");
/// driver.OnRunEvent += (runId, evt) => /* drive a watchdog / UI */;
/// var (run, error) = await driver.StartAsync(new CliRunRequest
/// {
///     RunId = "run-1",
///     Prompt = "Refactor the parser",
///     WorkingDirectory = @"C:\repo",
/// });
/// </code>
/// </summary>
public sealed class CliRunner
{
    private readonly Dictionary<string, ICliDriver> _drivers;

    /// <summary>Build a runner with drivers for every supported CLI sharing the given options/providers.</summary>
    public CliRunner(
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null)
    {
        _drivers = new Dictionary<string, ICliDriver>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude]      = new ClaudeDriver(options, logger, logPaths, home),
            [CliTypes.Codex]       = new CodexDriver(options, logger, logPaths, home),
            [CliTypes.Gemini]      = new GeminiDriver(options, logger, logPaths, home),
            [CliTypes.Antigravity] = new AntigravityDriver(options, logger, logPaths, home),
        };
    }

    // ── Typed accessors (sugar over Get) — for code that statically knows the CLI ──

    /// <summary>The Claude Code driver. Sugar for <c>Get(CliTypes.Claude)</c>.</summary>
    public ICliDriver Claude => Get(CliTypes.Claude);
    /// <summary>The OpenAI Codex driver. Sugar for <c>Get(CliTypes.Codex)</c>.</summary>
    public ICliDriver Codex => Get(CliTypes.Codex);
    /// <summary>
    /// The Google Gemini driver. Sugar for <c>Get(CliTypes.Gemini)</c>.
    /// <para><b>Deprecated</b> — unused and unmaintained. <b>Antigravity</b> (Google's
    /// agentic CLI) is the planned Google integration that supersedes it.</para>
    /// </summary>
    public ICliDriver Gemini => Get(CliTypes.Gemini);
    /// <summary>The Google Antigravity (agentapi) driver. Sugar for <c>Get(CliTypes.Antigravity)</c>.</summary>
    public ICliDriver Antigravity => Get(CliTypes.Antigravity);

    /// <summary>The drivers, one per supported CLI.</summary>
    public IReadOnlyCollection<ICliDriver> Drivers => _drivers.Values;

    /// <summary>The CLI types this runner can resolve.</summary>
    public IReadOnlyCollection<string> SupportedCliTypes => _drivers.Keys;

    // ── String resolution — for config-driven / dynamic selection ──

    /// <summary>
    /// Resolve the driver for <paramref name="cliType"/> (case-insensitive, exact
    /// match against the registered drivers). Throws <see cref="ArgumentException"/>
    /// when the type is unknown — it does NOT silently fall back to a default, so a
    /// bad config value surfaces instead of selecting the wrong CLI. The same object
    /// the typed accessors return.
    /// </summary>
    public ICliDriver Get(string? cliType)
        => TryGet(cliType, out var driver)
            ? driver
            : throw new ArgumentException($"No driver for CLI type '{cliType}'. Known: {string.Join(", ", _drivers.Keys)}.", nameof(cliType));

    /// <summary>
    /// Try to resolve the driver for <paramref name="cliType"/> (case-insensitive,
    /// exact match). Returns false for an unknown / empty type — no fallback.
    /// </summary>
    public bool TryGet(string? cliType, out ICliDriver driver)
    {
        if (!string.IsNullOrWhiteSpace(cliType) && _drivers.TryGetValue(cliType.Trim(), out driver!))
            return true;
        driver = null!;
        return false;
    }
}

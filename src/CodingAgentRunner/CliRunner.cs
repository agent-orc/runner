using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Diagnostics;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;

namespace CodingAgentRunner;

/// <summary>
/// The entry point: builds and holds one <see cref="ICliDriver"/> per supported CLI
/// from a single set of options, so a consumer wires the library once and then
/// resolves a driver by CLI type. Each driver is one <see cref="CliRunEngine"/>
/// parameterized by a built-in <see cref="CliDescriptor"/> — there are no per-CLI
/// subclasses; adding a CLI is a descriptor in the catalog.
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
    private readonly Dictionary<string, ICliModelDiscovery> _modelDiscoveries;
    private readonly ConcurrentDictionary<string, ModelCatalogCacheEntry> _modelCatalogCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _modelDiscoveryLocks =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly CliOptions _options;
    private readonly IUserHomeProvider _home;

    /// <summary>Build a runner with an engine for every built-in CLI sharing the given options/providers.</summary>
    public CliRunner(
        CliOptions? options = null,
        ILogger? logger = null,
        IRunLogPathProvider? logPaths = null,
        IUserHomeProvider? home = null,
        IEnumerable<ICliModelDiscovery>? modelDiscoveries = null)
    {
        _options = options ?? new CliOptions();
        var catalog = BuiltInDescriptors.DefaultCatalog();
        _home = home ?? new DefaultUserHomeProvider();
        _drivers = new Dictionary<string, ICliDriver>(StringComparer.OrdinalIgnoreCase);
        foreach (var type in catalog.Available)
            _drivers[type] = new CliRunEngine(catalog.Get(type), _options, logger, logPaths, _home);

        _modelDiscoveries = new Dictionary<string, ICliModelDiscovery>(StringComparer.OrdinalIgnoreCase)
        {
            [CliTypes.Claude] = new ClaudeCliModelDiscovery(_options, _options.ModelDiscoveryTimeProvider),
            [CliTypes.Codex] = new CodexCliModelDiscovery(_options, _options.ModelDiscoveryTimeProvider),
        };
        if (modelDiscoveries is not null)
        {
            foreach (var discovery in modelDiscoveries)
            {
                ArgumentNullException.ThrowIfNull(discovery);
                if (string.IsNullOrWhiteSpace(discovery.CliType))
                    throw new ArgumentException("A model discovery service must name its CLI type.", nameof(modelDiscoveries));
                _modelDiscoveries[discovery.CliType.Trim()] = discovery;
            }
        }
    }

    // ── Typed accessors (sugar over Get) — for code that statically knows the CLI ──

    /// <summary>The Claude Code driver. Sugar for <c>Get(CliTypes.Claude)</c>.</summary>
    public ICliDriver Claude => Get(CliTypes.Claude);
    /// <summary>The OpenAI Codex driver. Sugar for <c>Get(CliTypes.Codex)</c>.</summary>
    public ICliDriver Codex => Get(CliTypes.Codex);
    /// <summary>
    /// The Google Gemini driver. Sugar for <c>Get(CliTypes.Gemini)</c>.
    /// <para><b>Deprecated and unsupported</b> — unmaintained; removal is planned
    /// before 1.0. <b>Antigravity</b> (Google's agentic CLI) supersedes it.</para>
    /// </summary>
    [Obsolete("Gemini CLI support is deprecated and unmaintained; removal is planned before 1.0. Use Antigravity (agentapi) for Google models.")]
#pragma warning disable CS0618
    public ICliDriver Gemini => Get(CliTypes.Gemini);
#pragma warning restore CS0618
    /// <summary>The Google Antigravity (agentapi) driver. Sugar for <c>Get(CliTypes.Antigravity)</c>.</summary>
    public ICliDriver Antigravity => Get(CliTypes.Antigravity);

    /// <summary>The drivers, one per supported CLI.</summary>
    public IReadOnlyCollection<ICliDriver> Drivers => _drivers.Values;

    /// <summary>
    /// Probe the machine: which CLIs are installed (via each driver's
    /// <c>--version</c> probe), whether a credential source is present (credential
    /// file or API-key env var), and whether Node.js/npm are available. The report
    /// carries the install commands and sign-in steps for anything missing, and
    /// <see cref="Diagnostics.EnvironmentReport.ToText"/> renders it for a log or
    /// terminal. Blocking; probes run concurrently and each is bounded by an ~8 s
    /// timeout, so a fully healthy machine answers in a few seconds.
    /// </summary>
    public EnvironmentReport InspectEnvironment() => EnvironmentInspector.Inspect(Drivers, _home);

    /// <summary>
    /// Discover the models offered by the installed CLI and merge them with
    /// <see cref="KnownModels"/>. Known models absent from the live response remain
    /// in the catalog with <see cref="CliModelInfo.Available"/> set to false.
    /// Results are cached in memory for <see cref="CliOptions.ModelDiscoveryCacheTtl"/>;
    /// <paramref name="forceRefresh"/> bypasses the cached value.
    /// </summary>
    public async Task<CliModelCatalog> DiscoverModelsAsync(
        string cliType,
        bool forceRefresh = false,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(cliType)
            || !_modelDiscoveries.TryGetValue(cliType.Trim(), out var discovery))
            throw new ArgumentException(
                $"No model discovery service for CLI type '{cliType}'. Known: {string.Join(", ", _modelDiscoveries.Keys)}.",
                nameof(cliType));

        var key = discovery.CliType;
        var now = _options.ModelDiscoveryTimeProvider.GetUtcNow();
        if (!forceRefresh
            && _modelCatalogCache.TryGetValue(key, out var cached)
            && cached.ExpiresAt > now)
            return cached.Catalog;

        var gate = _modelDiscoveryLocks.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            now = _options.ModelDiscoveryTimeProvider.GetUtcNow();
            if (!forceRefresh
                && _modelCatalogCache.TryGetValue(key, out cached)
                && cached.ExpiresAt > now)
                return cached.Catalog;

            var catalog = await discovery.DiscoverAsync(ct).ConfigureAwait(false);
            now = _options.ModelDiscoveryTimeProvider.GetUtcNow();
            var ttl = _options.ModelDiscoveryCacheTtl < TimeSpan.Zero
                ? TimeSpan.Zero
                : _options.ModelDiscoveryCacheTtl;
            _modelCatalogCache[key] = new ModelCatalogCacheEntry(catalog, now + ttl);
            return catalog;
        }
        finally
        {
            gate.Release();
        }
    }

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

    private sealed record ModelCatalogCacheEntry(CliModelCatalog Catalog, DateTimeOffset ExpiresAt);
}

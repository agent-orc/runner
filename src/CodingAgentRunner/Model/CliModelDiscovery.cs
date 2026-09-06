using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Execution.Hardening;

namespace CodingAgentRunner.Model;

/// <summary>Discovers the models offered by one installed coding-agent CLI.</summary>
public interface ICliModelDiscovery
{
    /// <summary>The CLI type this discovery service probes.</summary>
    string CliType { get; }

    /// <summary>Probe the CLI and return its catalog merged with <see cref="KnownModels"/>.</summary>
    Task<CliModelCatalog> DiscoverAsync(CancellationToken ct = default);
}

/// <summary>Discovers Codex models through <c>codex debug models</c>.</summary>
public sealed class CodexCliModelDiscovery : ICliModelDiscovery
{
    private readonly CliOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Create a Codex discovery service using the runner's launch options.</summary>
    public CodexCliModelDiscovery(CliOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new CliOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string CliType => CliTypes.Codex;

    /// <inheritdoc />
    public async Task<CliModelCatalog> DiscoverAsync(CancellationToken ct = default)
    {
        var executable = _options.CodexPath ?? "codex";
        var version = await DiscoverVersionAsync(executable, ct).ConfigureAwait(false);

        try
        {
            var result = await CliModelDiscoveryProcess.RunAsync(
                executable,
                ["debug", "models"],
                _options,
                ct).ConfigureAwait(false);

            if (result.ExitCode != 0)
                return CliModelCatalogMerger.Merge(
                    CliTypes.Codex, [], version, _timeProvider.GetUtcNow().UtcDateTime);

            var listed = ParseModels(result.Stdout);
            return CliModelCatalogMerger.Merge(
                CliTypes.Codex, listed, version, _timeProvider.GetUtcNow().UtcDateTime);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Discovery is an availability probe. A missing, old, or temporarily
            // unhealthy CLI yields a disabled known catalog instead of hiding it.
            return CliModelCatalogMerger.Merge(
                CliTypes.Codex, [], version, _timeProvider.GetUtcNow().UtcDateTime);
        }
    }

    internal static IReadOnlyList<CliModelInfo> ParseModels(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("models", out var models)
            || models.ValueKind != JsonValueKind.Array)
            throw new JsonException("Codex model output does not contain a models array.");

        var parsed = new List<(int Index, CliModelInfo Model)>();
        var index = 0;
        foreach (var element in models.EnumerateArray())
        {
            var currentIndex = index++;
            var slug = String(element, "slug");
            if (string.IsNullOrWhiteSpace(slug)) continue;

            var visibility = String(element, "visibility");
            if (string.Equals(visibility, "hide", StringComparison.OrdinalIgnoreCase))
                continue;

            var levels = new List<string>();
            var reasoningKnown = element.TryGetProperty("supported_reasoning_levels", out var reasoning)
                                 && reasoning.ValueKind == JsonValueKind.Array;
            if (reasoningKnown)
            {
                foreach (var level in reasoning.EnumerateArray())
                {
                    var effort = level.ValueKind == JsonValueKind.Object
                        ? String(level, "effort")
                        : level.ValueKind == JsonValueKind.String ? level.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(effort)) levels.Add(effort!);
                }
            }

            var speedTiers = new List<string>();
            if (element.TryGetProperty("additional_speed_tiers", out var speeds)
                && speeds.ValueKind == JsonValueKind.Array)
            {
                speedTiers.AddRange(speeds.EnumerateArray()
                    .Where(value => value.ValueKind == JsonValueKind.String)
                    .Select(value => value.GetString())
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Select(value => value!));
            }

            var serviceTiers = new List<CliModelServiceTier>();
            if (element.TryGetProperty("service_tiers", out var tiers)
                && tiers.ValueKind == JsonValueKind.Array)
            {
                foreach (var tier in tiers.EnumerateArray())
                {
                    if (tier.ValueKind == JsonValueKind.String && tier.GetString() is { Length: > 0 } stringTierId)
                    {
                        serviceTiers.Add(new CliModelServiceTier { Id = stringTierId, Name = stringTierId });
                    }
                    else if (tier.ValueKind == JsonValueKind.Object)
                    {
                        var tierId = String(tier, "id") ?? "";
                        serviceTiers.Add(new CliModelServiceTier
                        {
                            Id = tierId,
                            Name = String(tier, "name") ?? tierId,
                            Description = String(tier, "description"),
                        });
                    }
                }
            }

            parsed.Add((currentIndex, new CliModelInfo
            {
                Id = slug!,
                Label = String(element, "display_name") ?? slug!,
                Description = String(element, "description"),
                Vendor = "openai",
                ContextWindow = Integer(element, "context_window"),
                Priority = Integer(element, "priority"),
                Visibility = visibility,
                ThinkingLevels = levels,
                ThinkingLevelsDiscovered = reasoningKnown,
                DefaultThinkingLevel = String(element, "default_reasoning_level"),
                AdditionalSpeedTiers = speedTiers,
                ServiceTiers = serviceTiers,
                Available = true,
            }));
        }

        return parsed
            .OrderBy(item => item.Model.Priority ?? int.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Model)
            .DistinctBy(model => KnownModels.NormalizeKey(model.Id))
            .ToList();
    }

    private async Task<string?> DiscoverVersionAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await CliModelDiscoveryProcess.RunAsync(
                executable, ["--version"], _options, cancellationToken).ConfigureAwait(false);
            if (result.ExitCode != 0) return null;
            return ParseVersion(string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    internal static string? ParseVersion(string value)
        => Regex.Match(value, @"\b\d+\.\d+\.\d+(?:[-+][0-9A-Za-z.-]+)?\b").Value is { Length: > 0 } version
            ? version
            : null;

    private static string? String(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? Integer(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.TryGetInt32(out var number)
            ? number
            : null;
}

/// <summary>
/// Reports the known Claude registry based on whether Claude Code is installed.
/// Claude has no model-list command; a future probe can replace this service through
/// the <see cref="ICliModelDiscovery"/> seam.
/// </summary>
public sealed class ClaudeCliModelDiscovery : ICliModelDiscovery
{
    private readonly CliOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Create a Claude discovery service using the runner's launch options.</summary>
    public ClaudeCliModelDiscovery(CliOptions? options = null, TimeProvider? timeProvider = null)
    {
        _options = options ?? new CliOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public string CliType => CliTypes.Claude;

    /// <inheritdoc />
    public async Task<CliModelCatalog> DiscoverAsync(CancellationToken ct = default)
    {
        var executable = _options.ClaudePath ?? "claude";
        string? version = null;
        var installed = false;
        try
        {
            var result = await CliModelDiscoveryProcess.RunAsync(
                executable, ["--version"], _options, ct).ConfigureAwait(false);
            installed = result.ExitCode == 0;
            if (installed)
                version = CodexCliModelDiscovery.ParseVersion(
                    string.IsNullOrWhiteSpace(result.Stdout) ? result.Stderr : result.Stdout);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Presence is the only signal Claude currently provides.
        }

        var displayVersion = version is null ? "an unknown version" : $"{version}";
        var note = installed
            ? $"Claude Code {displayVersion} does not expose model discovery; availability is based on CLI presence."
            : "Claude Code is not installed or did not answer its version probe.";
        var models = KnownModels.For(CliTypes.Claude)
            .Select(known => CliModelCatalogMerger.FromKnown(known, installed, note))
            .ToList();

        return new CliModelCatalog
        {
            CliType = CliTypes.Claude,
            Models = models,
            Source = "known-models",
            CliVersion = version,
            FetchedAt = _timeProvider.GetUtcNow().UtcDateTime,
        };
    }
}

internal static class CliModelCatalogMerger
{
    public static CliModelCatalog Merge(
        string cliType,
        IReadOnlyList<CliModelInfo> listed,
        string? cliVersion,
        DateTime fetchedAt)
    {
        var versionLabel = cliVersion is null ? "an unknown version" : cliVersion;
        var merged = new List<CliModelInfo>();
        var matchedKnown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var discovered in listed)
        {
            var known = KnownModels.Find(cliType, discovered.Id);
            if (known is null)
            {
                merged.Add(discovered with
                {
                    Label = string.IsNullOrWhiteSpace(discovered.Label) ? discovered.Id : discovered.Label,
                    Vendor = discovered.Vendor ?? VendorFor(cliType),
                    Available = true,
                    AvailabilityNote = $"Reported by {DisplayCli(cliType)} {versionLabel}; this model is not in KnownModels.",
                });
                continue;
            }

            matchedKnown.Add(known.Id);
            var aliases = known.Aliases
                .Where(alias => KnownModels.NormalizeKey(alias) != KnownModels.NormalizeKey(discovered.Id))
                .ToList();
            if (KnownModels.NormalizeKey(known.Id) != KnownModels.NormalizeKey(discovered.Id))
                aliases.Insert(0, known.Id);
            merged.Add(discovered with
            {
                Label = string.IsNullOrWhiteSpace(discovered.Label) ? known.Label : discovered.Label,
                Vendor = known.Vendor,
                ContextWindow = discovered.ContextWindow ?? known.ContextWindow,
                Aliases = aliases,
                GenerationOrder = known.GenerationOrder,
                Available = true,
                Deprecated = known.Deprecated,
                AvailabilityNote = null,
            });
        }

        foreach (var known in KnownModels.For(cliType))
        {
            if (matchedKnown.Contains(known.Id)) continue;
            merged.Add(FromKnown(
                known,
                available: false,
                $"Not offered by {DisplayCli(cliType)} {versionLabel}."));
        }

        return new CliModelCatalog
        {
            CliType = cliType,
            Models = merged,
            Source = "cli",
            CliVersion = cliVersion,
            FetchedAt = DateTime.SpecifyKind(fetchedAt, DateTimeKind.Utc),
        };
    }

    internal static CliModelInfo FromKnown(KnownModel known, bool available, string? availabilityNote) => new()
    {
        Id = known.Id,
        Label = known.Label,
        Vendor = known.Vendor,
        ContextWindow = known.ContextWindow,
        Aliases = known.Aliases.ToList(),
        GenerationOrder = known.GenerationOrder,
        ThinkingLevels = CliThinkingLevels.For(known.CliType, known.Id).ToList(),
        DefaultThinkingLevel = CliThinkingLevels.DefaultFor(known.CliType, known.Id),
        Available = available,
        Deprecated = known.Deprecated,
        AvailabilityNote = availabilityNote,
    };

    private static string DisplayCli(string cliType)
        => string.Equals(cliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
            ? "Codex CLI"
            : string.Equals(cliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
                ? "Claude Code"
                : $"{cliType} CLI";

    private static string? VendorFor(string cliType)
        => string.Equals(cliType, CliTypes.Codex, StringComparison.OrdinalIgnoreCase)
            ? "openai"
            : string.Equals(cliType, CliTypes.Claude, StringComparison.OrdinalIgnoreCase)
                ? "anthropic"
                : null;
}

internal sealed record CliModelDiscoveryProcessResult(int ExitCode, string Stdout, string Stderr);

internal static class CliModelDiscoveryProcess
{
    public static async Task<CliModelDiscoveryProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> argv,
        CliOptions options,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = BinaryResolver.Resolve(executable),
            RedirectStandardInput = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in argv) startInfo.ArgumentList.Add(argument);
        EnvironmentHardening.Apply(startInfo, options.Hardening, options.EnvironmentOverrides);

        var spawn = (options.Spawner ?? CliProcessSpawner.Default).Spawn(startInfo);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.ModelDiscoveryTimeout);
        try
        {
            var stdout = spawn.Stdout.ReadToEndAsync(timeout.Token);
            var stderr = spawn.Stderr.ReadToEndAsync(timeout.Token);
            await spawn.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return new CliModelDiscoveryProcessResult(
                spawn.Process.ExitCode,
                await stdout.ConfigureAwait(false),
                await stderr.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(spawn);
            throw new TimeoutException($"Model discovery exceeded {options.ModelDiscoveryTimeout}.");
        }
        catch
        {
            TryKill(spawn);
            throw;
        }
        finally
        {
            spawn.Stdin.Dispose();
            spawn.Stdout.Dispose();
            spawn.Stderr.Dispose();
            spawn.Process.Dispose();
        }
    }

    private static void TryKill(CliSpawn spawn)
    {
        try
        {
            if (spawn.Process.HasExited) return;
            if (spawn.KillOverride is not null)
                spawn.KillOverride(RunStopReason.Cancelled);
            else
                spawn.Process.Kill(entireProcessTree: true);
        }
        catch
        {
            // Best effort during timeout/error cleanup.
        }
    }
}

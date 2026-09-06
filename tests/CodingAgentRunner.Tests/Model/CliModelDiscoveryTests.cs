using System.Diagnostics;
using System.Text;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Tests.Model;

public class CliModelDiscoveryTests
{
    [Fact]
    public void CodexFixture01534_ParsesAllPublicMetadataAndFiltersHiddenEntries()
    {
        var listed = CodexCliModelDiscovery.ParseModels(Fixture("codex-0.153.4.json"));

        Assert.Equal(7, listed.Count);
        Assert.Equal("gpt-6-astra", listed[0].Id); // priority order
        Assert.DoesNotContain(listed, model => model.Id is "gpt-reserve" or "codex-auto-review");

        var astra = listed[0];
        Assert.Equal("GPT-6-Astra", astra.Label);
        Assert.Equal("Most capable agentic coding model.", astra.Description);
        Assert.Equal("list", astra.Visibility);
        Assert.Equal(1, astra.Priority);
        Assert.Equal(272_000, astra.ContextWindow);
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], astra.ThinkingLevels);
        Assert.Equal("medium", astra.DefaultThinkingLevel);
        Assert.True(astra.ThinkingLevelsDiscovered);
        Assert.Equal(["fast"], astra.AdditionalSpeedTiers);
        var tier = Assert.Single(astra.ServiceTiers);
        Assert.Equal("priority", tier.Id);
        Assert.Equal("Fast", tier.Name);
        Assert.NotNull(tier.Description);
    }

    [Fact]
    public void Merge_01534_MakesAstraAvailableWithKnownAndLiveMetadata()
    {
        var catalog = MergeFixture("codex-0.153.4.json", "0.153.4");
        var astra = Assert.IsType<CliModelInfo>(catalog.Find("gpt-6-astra"));

        Assert.True(astra.Available);
        Assert.Null(astra.AvailabilityNote);
        Assert.Equal("openai", astra.Vendor);
        Assert.Equal(6_000, astra.GenerationOrder);
        Assert.Equal(["low", "medium", "high", "xhigh", "max", "ultra"], astra.ThinkingLevels);
    }

    [Fact]
    public void Merge_01510_KeepsAstraDisabledAndNamesTheCliVersion()
    {
        var catalog = MergeFixture("codex-0.151.0.json", "0.151.0");
        var astra = Assert.IsType<CliModelInfo>(catalog.Find("gpt-6-astra"));

        Assert.False(astra.Available);
        Assert.Contains("0.151.0", astra.AvailabilityNote);
        Assert.Equal("gpt-5.6-sol", catalog.Models[0].Id); // live priority order remains first
    }

    [Fact]
    public void Merge_UnknownListedModelRemainsAvailableWithANote()
    {
        var listed = new[]
        {
            new CliModelInfo
            {
                Id = "gpt-new-preview",
                Label = "GPT New Preview",
                Priority = 2,
                ThinkingLevels = ["low"],
                ThinkingLevelsDiscovered = true,
            },
        };

        var catalog = CliModelCatalogMerger.Merge(CliTypes.Codex, listed, "0.200.0", DateTime.UtcNow);
        var preview = Assert.IsType<CliModelInfo>(catalog.Find("gpt-new-preview"));
        Assert.True(preview.Available);
        Assert.Contains("not in KnownModels", preview.AvailabilityNote);
    }

    [Fact]
    public void DiscoveredLadderOverridesStaticTableAndCarriesItsDefault()
    {
        var catalog = new CliModelCatalog
        {
            CliType = CliTypes.Codex,
            Models =
            [
                new CliModelInfo
                {
                    Id = "gpt-6-astra",
                    ThinkingLevels = ["low", "max"],
                    ThinkingLevelsDiscovered = true,
                    DefaultThinkingLevel = "low",
                },
            ],
        };

        Assert.Equal(["low", "max"], CliThinkingLevels.For(CliTypes.Codex, "gpt-6-astra", catalog));
        Assert.Equal("low", CliThinkingLevels.DefaultFor(CliTypes.Codex, "gpt-6-astra", catalog));
        Assert.Equal("low", CliThinkingLevels.Normalize(CliTypes.Codex, "gpt-6-astra", "ultra", catalog));
    }

    [Fact]
    public void CatalogWithoutDiscoveredLadderFallsBackToStaticTable()
    {
        var catalog = new CliModelCatalog
        {
            CliType = CliTypes.Codex,
            Models = [new CliModelInfo { Id = "gpt-6-astra", ThinkingLevelsDiscovered = false }],
        };

        Assert.Equal(
            ["low", "medium", "high", "xhigh", "max", "ultra"],
            CliThinkingLevels.For(CliTypes.Codex, "gpt-6-astra", catalog));
    }

    [Fact]
    public async Task RunnerDiscovery_UsesConfiguredPathHardenedPipeSpawnAndCachesResults()
    {
        var spawner = new FixtureSpawner(Fixture("codex-0.153.4.json"));
        var configuredPath = Path.Combine(Path.GetTempPath(), "custom", OperatingSystem.IsWindows() ? "codex.exe" : "codex");
        var runner = new CliRunner(new CliOptions
        {
            CodexPath = configuredPath,
            Spawner = spawner,
            ModelDiscoveryCacheTtl = TimeSpan.FromMinutes(5),
        });

        var first = await runner.DiscoverModelsAsync(CliTypes.Codex);
        var cached = await runner.DiscoverModelsAsync(CliTypes.Codex);

        Assert.Same(first, cached);
        Assert.Equal(2, spawner.Starts.Count); // --version + debug models
        Assert.All(spawner.Starts, start =>
        {
            Assert.Equal(configuredPath, start.FileName);
            Assert.False(start.RedirectStandardInput);
            Assert.True(start.RedirectStandardOutput);
            Assert.True(start.RedirectStandardError);
            Assert.False(start.UseShellExecute);
            Assert.True(start.CreateNoWindow);
            Assert.Equal("1", start.Environment["CI"]);
            Assert.Equal("1", start.Environment["NO_COLOR"]);
        });
        Assert.Equal(["--version"], spawner.Starts[0].Arguments);
        Assert.Equal(["debug", "models"], spawner.Starts[1].Arguments);
        Assert.Equal("0.153.4", first.CliVersion);
        Assert.True(first.Find("gpt-6-astra")!.Available);

        _ = await runner.DiscoverModelsAsync(CliTypes.Codex, forceRefresh: true);
        Assert.Equal(4, spawner.Starts.Count);
    }

    [Fact]
    public async Task ClaudeDiscovery_UsesRegistryAndMarksAvailabilityByCliPresence()
    {
        var spawner = new FixtureSpawner(Fixture("codex-0.153.4.json"));
        var runner = new CliRunner(new CliOptions { Spawner = spawner });

        var catalog = await runner.DiscoverModelsAsync(CliTypes.Claude);

        Assert.Equal("known-models", catalog.Source);
        Assert.All(catalog.Models, model =>
        {
            Assert.True(model.Available);
            Assert.Contains("CLI presence", model.AvailabilityNote);
            Assert.False(model.ThinkingLevelsDiscovered);
        });
    }

    private static CliModelCatalog MergeFixture(string name, string version)
        => CliModelCatalogMerger.Merge(
            CliTypes.Codex,
            CodexCliModelDiscovery.ParseModels(Fixture(name)),
            version,
            new DateTime(2026, 9, 6, 0, 0, 0, DateTimeKind.Utc));

    private static string Fixture(string name)
        => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "model-discovery", name));

    private sealed class FixtureSpawner(string modelsJson) : ICliProcessSpawner
    {
        public List<ObservedStart> Starts { get; } = [];

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            var arguments = startInfo.ArgumentList.ToList();
            Starts.Add(new ObservedStart(
                startInfo.FileName,
                arguments,
                startInfo.RedirectStandardInput,
                startInfo.RedirectStandardOutput,
                startInfo.RedirectStandardError,
                startInfo.UseShellExecute,
                startInfo.CreateNoWindow,
                startInfo.Environment.ToDictionary(pair => pair.Key, pair => pair.Value)));

            var output = arguments.SequenceEqual(["--version"])
                ? startInfo.FileName.Contains("claude", StringComparison.OrdinalIgnoreCase)
                    ? "2.1.4 (Claude Code)\n"
                    : "codex-cli 0.153.4\n"
                : modelsJson;

            var helper = Process.Start(new ProcessStartInfo
            {
                FileName = DotnetPath(),
                Arguments = "--version",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            })!;
            return new CliSpawn(
                helper,
                Stream.Null,
                Reader(output),
                Reader(string.Empty));
        }

        private static StreamReader Reader(string value)
            => new(new MemoryStream(Encoding.UTF8.GetBytes(value)), Encoding.UTF8);

        private static string DotnetPath()
        {
            var executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
            var root = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            if (!string.IsNullOrWhiteSpace(root))
            {
                var candidate = Path.Combine(root, executable);
                if (File.Exists(candidate)) return candidate;
            }

            return Environment.ProcessPath
                   ?? throw new InvalidOperationException("The test host did not expose its process path.");
        }
    }

    private sealed record ObservedStart(
        string FileName,
        List<string> Arguments,
        bool RedirectStandardInput,
        bool RedirectStandardOutput,
        bool RedirectStandardError,
        bool UseShellExecute,
        bool CreateNoWindow,
        Dictionary<string, string?> Environment);
}

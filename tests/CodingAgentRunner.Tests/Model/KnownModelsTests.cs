using CodingAgentRunner.Model;

namespace CodingAgentRunner.Tests.Model;

public class KnownModelsTests
{
    [Theory]
    [InlineData(CliTypes.Codex, "gpt-6-sol")]
    [InlineData(CliTypes.Codex, "gpt-6-luna")]
    [InlineData(CliTypes.Codex, "gpt-6.1-sol")]
    [InlineData(CliTypes.Codex, "gpt-6.1-terra")]
    [InlineData(CliTypes.Claude, "claude-opus-5")]
    [InlineData(CliTypes.Claude, "claude-opus-5-5")]
    [InlineData(CliTypes.Claude, "claude-sonnet-5-5")]
    [InlineData(CliTypes.Claude, "claude-fable-5-1")]
    public void CurrentLineup_HasKnownEntry(string cli, string id)
        => Assert.NotNull(KnownModels.Find(cli, id));

    [Fact]
    public void RetiringIds_RemainResolvableButAreNotPreferred()
    {
        Assert.True(KnownModels.Find(CliTypes.Claude, "claude-sonnet-4-5-20250929")!.Deprecated);
        Assert.True(KnownModels.Find(CliTypes.Codex, "gpt-5")!.Deprecated);
        Assert.Equal("claude-sonnet-5", KnownModels.Find(CliTypes.Claude, "sonnet")!.Id);
        Assert.Equal("claude-opus-4-8", KnownModels.Find(CliTypes.Claude, "opus")!.Id);
        Assert.False(KnownModels.For(CliTypes.Claude).First().Deprecated);
        Assert.False(KnownModels.For(CliTypes.Codex).First().Deprecated);
        Assert.Equal(500_000, KnownModels.Find(CliTypes.Codex, "gpt-6.1-sol")!.ContextWindow);
        Assert.Equal(500_000, KnownModels.Find(CliTypes.Codex, "gpt-6.1-terra")!.ContextWindow);
    }

    [Fact]
    public void Registry_CoversClaudeAndCodexIncludingAstra()
    {
        Assert.NotEmpty(KnownModels.For(CliTypes.Claude));
        Assert.NotEmpty(KnownModels.For(CliTypes.Codex));

        var astra = KnownModels.Find(CliTypes.Codex, "gpt-6-astra");
        Assert.NotNull(astra);
        Assert.Equal("GPT-6-Astra", astra!.Label);
        Assert.Equal("openai", astra.Vendor);
        Assert.Equal(272_000, astra.ContextWindow);
        Assert.True(astra.GenerationOrder > KnownModels.Find(CliTypes.Codex, "gpt-5.6-sol")!.GenerationOrder);
        var haiku = Assert.IsType<KnownModel>(KnownModels.Find(CliTypes.Claude, "claude-haiku-5-5"));
        Assert.Equal("Claude Haiku 5.5", haiku.Label);
        Assert.Equal(1_000_000, haiku.ContextWindow);
    }

    [Theory]
    [InlineData("claude-fable-5-1", 1_000_000)]
    [InlineData("claude-opus-5-5", 1_000_000)]
    [InlineData("claude-opus-5", 1_000_000)]
    [InlineData("claude-sonnet-5-5", 1_000_000)]
    [InlineData("claude-haiku-5-5", 1_000_000)]
    [InlineData("claude-opus-4-8", 1_000_000)]
    [InlineData("claude-opus-4-7", 1_000_000)]
    [InlineData("claude-opus-4-6", 200_000)]
    [InlineData("claude-sonnet-4-6", 200_000)]
    [InlineData("claude-opus-4-5", 200_000)]
    [InlineData("claude-sonnet-4-5", 200_000)]
    [InlineData("claude-haiku-4-5", 200_000)]
    [InlineData("claude-opus-4-1", 200_000)]
    public void Claude_ContextWindowMatchesModel(string id, int expected)
        => Assert.Equal(expected, KnownModels.Find(CliTypes.Claude, id)!.ContextWindow);

    [Fact]
    public void Registry_ResolvesAliasesAndDotDashVariants()
    {
        Assert.Equal(
            "claude-haiku-4-5",
            KnownModels.Find(CliTypes.Claude, "claude-haiku-4-5-20251001")?.Id);
        Assert.Equal(
            "gpt-5.6-sol",
            KnownModels.Find(CliTypes.Codex, "GPT-5-6-SOL")?.Id);
    }

    [Fact]
    public void Registry_OrderingIsNewestFirstWithinCli()
    {
        var claude = KnownModels.For(CliTypes.Claude);
        Assert.Equal("claude-sonnet-5-5", claude[0].Id);
        Assert.Equal(claude.OrderByDescending(model => model.GenerationOrder), claude);
        var codex = KnownModels.For(CliTypes.Codex);
        Assert.Equal(codex.OrderByDescending(model => model.GenerationOrder), codex);
    }
}

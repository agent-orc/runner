using CodingAgentRunner.Model;

namespace CodingAgentRunner.Tests.Model;

public class KnownModelsTests
{
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
    }

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
        var codex = KnownModels.For(CliTypes.Codex);
        Assert.Equal(codex.OrderByDescending(model => model.GenerationOrder), codex);
    }
}

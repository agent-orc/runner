using CodingAgentRunner;
using CodingAgentRunner.Backends;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests;

public class CliRunnerTests
{
    [Fact]
    public void Get_ResolvesEachSupportedCli()
    {
        var runner = new CliRunner();
        Assert.IsType<ClaudeBackend>(runner.Get("claude"));
        Assert.IsType<CodexBackend>(runner.Get("codex"));
        Assert.IsType<GeminiBackend>(runner.Get("gemini"));
        Assert.IsType<CopilotBackend>(runner.Get("copilot"));
    }

    [Fact]
    public void Get_NormalizesTheCliType()
    {
        var runner = new CliRunner();
        Assert.Equal(CliTypes.Claude, runner.Get("CLAUDE").CliType);
    }

    [Fact]
    public void TryGet_UnknownFallsBackToNormalizedDefault_GetNeverThrowsForKnownTokens()
    {
        var runner = new CliRunner();
        // CliTypes.Normalize folds unknown values to copilot, so an odd token resolves.
        Assert.True(runner.TryGet("nonsense", out var backend));
        Assert.Equal(CliTypes.Copilot, backend.CliType);
    }

    [Fact]
    public void Backends_CoverAllFour()
    {
        var runner = new CliRunner();
        Assert.Equal(4, runner.Backends.Count);
    }
}

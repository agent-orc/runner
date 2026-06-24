using CodingAgentRunner;
using CodingAgentRunner.Drivers;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests;

public class CliRunnerTests
{
    [Fact]
    public void Get_ResolvesEachSupportedCli()
    {
        var runner = new CliRunner();
        Assert.IsType<ClaudeDriver>(runner.Get("claude"));
        Assert.IsType<CodexDriver>(runner.Get("codex"));
        Assert.IsType<GeminiDriver>(runner.Get("gemini"));
        Assert.IsType<AntigravityDriver>(runner.Get("antigravity"));
    }

    [Fact]
    public void Get_NormalizesTheCliType()
    {
        var runner = new CliRunner();
        Assert.Equal(CliTypes.Claude, runner.Get("CLAUDE").CliType);
    }

    [Fact]
    public void TypedAccessors_ResolveTheRightDriver()
    {
        var runner = new CliRunner();
        Assert.Equal(CliTypes.Claude, runner.Claude.CliType);
        Assert.Equal(CliTypes.Codex, runner.Codex.CliType);
        Assert.Equal(CliTypes.Gemini, runner.Gemini.CliType);
        Assert.Equal(CliTypes.Antigravity, runner.Antigravity.CliType);
    }

    [Fact]
    public void TypedAccessor_AndGetString_ReturnTheSameObject()
    {
        var runner = new CliRunner();
        Assert.Same(runner.Claude, runner.Get("claude"));   // sugar over Get — one mechanism
    }

    [Fact]
    public void UnknownCli_FailsClearly_NoSilentFallback()
    {
        var runner = new CliRunner();
        Assert.False(runner.TryGet("nonsense", out _));      // no silent fallback
        Assert.False(runner.TryGet("", out _));
        Assert.False(runner.TryGet(null, out _));
        Assert.Throws<ArgumentException>(() => runner.Get("nonsense"));
    }

    [Fact]
    public void Drivers_CoverEverySupportedCli()
    {
        var runner = new CliRunner();
        Assert.Equal(4, runner.Drivers.Count);   // claude, codex, gemini, antigravity
    }
}

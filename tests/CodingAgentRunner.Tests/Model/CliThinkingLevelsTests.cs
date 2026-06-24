using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Model;

public class CliThinkingLevelsTests
{
    [Theory]
    // Real Claude ids → the right ladder.
    [InlineData("claude-opus-4-8", "low,medium,high,xhigh,max")]
    [InlineData("claude-opus-4-7", "low,medium,high,xhigh,max")]
    [InlineData("claude-opus-4-6", "low,medium,high,max")]
    [InlineData("claude-opus-4-5", "low,medium,high,max")]
    [InlineData("claude-sonnet-4-6", "low,medium,high")]
    [InlineData("claude-haiku-4-5", "")]
    [InlineData("claude-opus-9-9", "low,medium,high,max")]      // unknown opus → max ladder (StartsWith fallback)
    [InlineData("claude-sonnet-9-9", "low,medium,high")]
    [InlineData("claude-opus-4.8", "low,medium,high,xhigh,max")]// dots normalize to dashes
    // False positives → NO ladder (the claude- prefix gate).
    [InlineData("my-opus-4-8-clone", "")]
    [InlineData("opus-4-8", "")]                                // bare, no claude- prefix
    [InlineData("claude - opus - 4 - 8", "")]                   // internal whitespace → malformed
    [InlineData("", "")]
    public void Claude_LadderPerModel(string model, string expectedCsv)
        => Assert.Equal(Split(expectedCsv), CliThinkingLevels.For("claude", model));

    [Theory]
    [InlineData("gpt-5.5", "minimal,low,medium,high,xhigh")]
    [InlineData("gpt-5-5", "minimal,low,medium,high,xhigh")]
    [InlineData("gpt-5", "minimal,low,medium,high")]
    [InlineData("gpt-5-codex", "minimal,low,medium,high")]
    [InlineData("gpt-6", "minimal,low,medium,high,xhigh")]
    // Foreign models routed to codex → NO ladder, even with dots.
    [InlineData("claude-opus-4-8", "")]
    [InlineData("claude.opus.4.8", "")]                         // dots: still recognized as foreign (the fix)
    [InlineData("gemini-pro", "")]
    public void Codex_LadderPerModel(string model, string expectedCsv)
        => Assert.Equal(Split(expectedCsv), CliThinkingLevels.For("codex", model));

    [Fact]
    public void Gemini_NeverHasLevels()
    {
        Assert.Empty(CliThinkingLevels.For("gemini", "gemini-pro"));
    }

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = CliThinkingLevels.Normalize("claude", "claude-opus-4-8", "xhigh");
        Assert.Equal(once, CliThinkingLevels.Normalize("claude", "claude-opus-4-8", once));
    }

    private static string[] Split(string csv)
        => csv.Length == 0 ? System.Array.Empty<string>() : csv.Split(',');
}

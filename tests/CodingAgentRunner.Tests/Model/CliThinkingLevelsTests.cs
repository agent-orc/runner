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
    [InlineData("claude-opus-5", "low,medium,high,xhigh,max")]
    [InlineData("claude-opus-5-5", "low,medium,high,xhigh,max")]
    [InlineData("claude-fable-5-1", "low,medium,high,xhigh,max")]
    [InlineData("claude-sonnet-5", "low,medium,high,xhigh,max")]
    [InlineData("claude-sonnet-5-5", "low,medium,high,xhigh,max")] // point release matches the sonnet-5 substring
    [InlineData("claude-sonnet-5.5", "low,medium,high,xhigh,max")] // dots normalize to dashes
    [InlineData("claude-sonnet-4-6", "low,medium,high")]
    [InlineData("claude-sonnet-4-5", "low,medium,high")]          // must NOT hit the sonnet-5 rule
    [InlineData("claude-haiku-4-5", "")]
    [InlineData("claude-haiku-5-5", "low,medium,high,xhigh,max")]
    [InlineData("claude-opus-9-9", "low,medium,high,max")]      // unknown opus → max ladder (StartsWith fallback)
    [InlineData("claude-sonnet-9-9", "low,medium,high")]
    [InlineData("claude-opus-4.8", "low,medium,high,xhigh,max")] // dots normalize to dashes
    // False positives → NO ladder (the claude- prefix gate).
    [InlineData("my-opus-4-8-clone", "")]
    [InlineData("opus-4-8", "")]                                // bare, no claude- prefix
    [InlineData("claude - opus - 4 - 8", "")]                   // internal whitespace → malformed
    [InlineData("", "")]
    public void Claude_LadderPerModel(string model, string expectedCsv)
        => Assert.Equal(Split(expectedCsv), CliThinkingLevels.For("claude", model));

    [Theory]
    [InlineData("gpt-5.5", "low,medium,high,xhigh")]
    [InlineData("gpt-5-5", "low,medium,high,xhigh")]
    [InlineData("gpt-5", "low,medium,high")]
    [InlineData("gpt-5-codex", "low,medium,high")]
    [InlineData("gpt-6", "low,medium,high")]
    [InlineData("gpt-6-astra", "low,medium,high,xhigh,max,ultra")]
    [InlineData("gpt-6-sol", "low,medium,high,xhigh,max,ultra")]
    [InlineData("gpt-6-luna", "low,medium,high,xhigh,max")]
    [InlineData("gpt-6.1-sol", "low,medium,high,xhigh,max")]
    [InlineData("gpt-6.1-terra", "none,low,medium,high,xhigh,max")]
    [InlineData("gpt-6-1-terra", "none,low,medium,high,xhigh,max")]
    // gpt-5.6 includes max; Luna stops there while the other variants include ultra.
    [InlineData("gpt-5.6-sol", "low,medium,high,xhigh,max,ultra")]
    [InlineData("gpt-5.6-terra", "low,medium,high,xhigh,max,ultra")]
    [InlineData("gpt-5.6-luna", "low,medium,high,xhigh,max")]
    [InlineData("gpt-5.6", "low,medium,high,xhigh,max,ultra")]
    [InlineData("gpt-5-6-sol", "low,medium,high,xhigh,max,ultra")]  // already dash-form
    [InlineData("gpt-5.5-sol", "low,medium,high,xhigh")]            // 5.5 is xhigh, NOT ultra
    [InlineData("my-gpt-6-sol-clone", "low,medium,high")]
    [InlineData("gpt-6-luna-preview", "low,medium,high")]
    // Foreign models routed to codex → NO ladder, even with dots.
    [InlineData("claude-opus-4-8", "")]
    [InlineData("claude.opus.4.8", "")]                         // dots: still recognized as foreign (the fix)
    [InlineData("gemini-pro", "")]
    public void Codex_LadderPerModel(string model, string expectedCsv)
        => Assert.Equal(Split(expectedCsv), CliThinkingLevels.For("codex", model));

    [Theory]
    [InlineData("gpt-6-sol", "medium")]
    [InlineData("gpt-6-astra", "medium")]
    [InlineData("gpt-6-luna", "medium")]
    [InlineData("gpt-5.6-sol", "medium")]
    [InlineData("gpt-5.6-terra", "medium")]
    [InlineData("gpt-5.6-luna", "medium")]
    [InlineData("gpt-6.1-sol", "medium")]
    [InlineData("gpt-6.1-terra", "medium")]
    public void Codex_DefaultPerModel(string model, string expected)
        => Assert.Equal(expected, CliThinkingLevels.DefaultFor("codex", model));

    [Fact]
    public void Codex_UnsupportedRungFallsBackToModelDefault()
    {
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-6-luna", "ultra"));
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-6.1-sol", "ultra"));
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-6.1-terra", "ultra"));
        Assert.Equal("none", CliThinkingLevels.Normalize("codex", "gpt-6.1-terra", "none"));
        Assert.Equal("max", CliThinkingLevels.Normalize("codex", "gpt-6.1-sol", "max"));
        Assert.Equal("max", CliThinkingLevels.Normalize("codex", "gpt-6.1-terra", "max"));
    }

    [Fact]
    public void Gemini_NeverHasLevels()
    {
        Assert.Empty(CliThinkingLevels.For("gemini", "gemini-pro"));
    }

    [Theory]
    [InlineData("claude-haiku-5-5", "medium")]
    [InlineData("claude-opus-5-5", "medium")]
    [InlineData("claude-opus-5", "high")]
    [InlineData("claude-fable-5-1", "high")]
    public void Claude_DefaultPerModel(string model, string expected)
        => Assert.Equal(expected, CliThinkingLevels.DefaultFor("claude", model));

    [Theory]
    [InlineData("minimal", "Minimal")]
    [InlineData("none", "None")]
    [InlineData("xhigh", "Extra High")]
    [InlineData("ultra", "Ultra")]
    [InlineData("ULTRA", "Ultra")]     // case-insensitive
    [InlineData("max", "Max")]
    [InlineData("mystery", "mystery")] // unknown rung echoed back, never dropped
    public void DisplayName_LabelsEachRung(string level, string expected)
        => Assert.Equal(expected, CliThinkingLevels.DisplayName(level));

    [Fact]
    public void Normalize_IsIdempotent()
    {
        var once = CliThinkingLevels.Normalize("claude", "claude-opus-4-8", "xhigh");
        Assert.Equal(once, CliThinkingLevels.Normalize("claude", "claude-opus-4-8", once));
    }

    private static string[] Split(string csv)
        => csv.Length == 0 ? System.Array.Empty<string>() : csv.Split(',');
}

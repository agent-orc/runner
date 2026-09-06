using CodingAgentRunner;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Model;

/// <summary>
/// Regression guard for the model-change contract: the SAME requested thinking level
/// resolves differently per model — every run normalizes against the model's capability
/// table, so swapping the model can never smuggle an unsupported level into the argv.
/// </summary>
public class ThinkingLevelNormalizationTests
{
    [Fact]
    public void XHigh_KeptOnOpus48_FallsToDefaultOnOpus46()
    {
        Assert.Equal("xhigh", CliThinkingLevels.Normalize("claude", "claude-opus-4-8", "xhigh"));
        // opus-4-6 tops out at max (no xhigh) → unsupported request falls back to the default (high).
        Assert.Equal("high", CliThinkingLevels.Normalize("claude", "claude-opus-4-6", "xhigh"));
    }

    [Fact]
    public void AnyLevel_OnHaiku_NormalizesToNull()   // no reasoning selector at all
    {
        Assert.Null(CliThinkingLevels.Normalize("claude", "claude-haiku-4-5", "high"));
        Assert.Null(CliThinkingLevels.Normalize("claude", "claude-haiku-4-5", "xhigh"));
    }

    [Fact]
    public void Codex_XHigh_OnGpt55_ButFallsToDefaultOnGpt5()
    {
        Assert.Equal("xhigh", CliThinkingLevels.Normalize("codex", "gpt-5.5", "xhigh"));
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-5", "xhigh"));   // gpt-5 tops at high → default medium
    }

    [Fact]
    public void Codex_Ultra_KeptOnGpt56_ButGatedOnOlderModels()
    {
        // gpt-5.6 family accepts the new top rung.
        Assert.Equal("ultra", CliThinkingLevels.Normalize("codex", "gpt-5.6-sol", "ultra"));
        Assert.Equal("ultra", CliThinkingLevels.Normalize("codex", "gpt-5.6", "ultra"));
        // Older codex models never see ultra: it falls back to their default (medium).
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-5.5", "ultra")); // xhigh-capable, not ultra
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-5", "ultra"));   // old model, gated
    }

    [Fact]
    public void Codex_Max_IsAcceptedByModelsThatOfferIt()
    {
        Assert.Equal("max", CliThinkingLevels.Normalize("codex", "gpt-6-astra", "max"));
        Assert.Equal("max", CliThinkingLevels.Normalize("codex", "gpt-5.6-luna", "max"));
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-5.5", "max"));
    }

    [Fact]
    public void Codex_UltraLevel_EmitsReasoningEffortFlag()
    {
        // The whole point of the ladder change: ultra reaches the argv instead of
        // silently falling back to the CLI config default.
        var flags = CliReasoningFlags.For("codex", "gpt-5.6-sol", "ultra");
        Assert.Equal(new[] { "-c", "model_reasoning_effort=\"ultra\"" }, flags);

        // A gated model gets its default effort, never ultra.
        Assert.DoesNotContain("model_reasoning_effort=\"ultra\"", CliReasoningFlags.For("codex", "gpt-5", "ultra"));
    }

    [Fact]
    public void JunkLevel_OnNoSelectorModel_StaysNull_NoFlags()
    {
        // A model with no selector normalizes any request (junk or ultra) to null,
        // so CliReasoningFlags emits nothing and the CLI default wins.
        Assert.Null(CliThinkingLevels.Normalize("codex", "claude-opus-4-8", "ultra")); // foreign → no selector
        Assert.Null(CliThinkingLevels.Normalize("claude", "claude-haiku-4-5", "banana"));
        Assert.Empty(CliReasoningFlags.For("claude", "claude-haiku-4-5", "banana"));
    }

    [Fact]
    public void UltraLevel_NeverLeaksIntoClaudeLadder()
    {
        // Ultra is a Codex-only rung; Claude tops out at max. A request for ultra on a
        // full-ladder Claude model falls back to its default (high), never emits ultra.
        Assert.Equal("high", CliThinkingLevels.Normalize("claude", "claude-opus-4-8", "ultra"));
        Assert.DoesNotContain("ultra", CliThinkingLevels.For("claude", "claude-opus-4-8"));
    }

    [Fact]
    public void UnknownLevel_FallsBackToTheModelDefault()
    {
        Assert.Equal("high", CliThinkingLevels.Normalize("claude", "claude-opus-4-8", "ludicrous"));
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-5.5", "ludicrous"));
    }

    [Fact]
    public void NullRequested_YieldsTheModelDefault()
    {
        Assert.Equal("high", CliThinkingLevels.Normalize("claude", "claude-opus-4-8", null));
        Assert.Equal("medium", CliThinkingLevels.Normalize("codex", "gpt-5.5", null));
    }

    [Fact]
    public void Capabilities_AdvertiseExactlyWhatNormalizeHonors()
    {
        var runner = new CliRunner();
        Assert.Contains("xhigh", runner.Claude.Capabilities("claude-opus-4-8").ThinkingLevels);
        Assert.DoesNotContain("xhigh", runner.Claude.Capabilities("claude-opus-4-6").ThinkingLevels);
        Assert.Empty(runner.Claude.Capabilities("claude-haiku-4-5").ThinkingLevels);
    }
}

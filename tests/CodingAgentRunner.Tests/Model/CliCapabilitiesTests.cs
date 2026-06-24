using CodingAgentRunner;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Model;

public class CliCapabilitiesTests
{
    private static readonly CliRunner Runner = new();

    [Fact]
    public void Claude_Opus48_ExposesFullReasoningLadder_CleanAndResume()
    {
        var caps = Runner.Claude.Capabilities("claude-opus-4-8");

        Assert.Equal(CliTypes.Claude, caps.CliType);
        Assert.Equal("claude-opus-4-8", caps.Model);
        Assert.Equal(
            new[] { CliThinkingLevels.Low, CliThinkingLevels.Medium, CliThinkingLevels.High, CliThinkingLevels.XHigh, CliThinkingLevels.Max },
            caps.ThinkingLevels);
        Assert.Equal(CliThinkingLevels.High, caps.DefaultThinkingLevel);
        Assert.True(caps.SupportsThinking);
        Assert.True(caps.SupportsCleanContext);
        Assert.True(caps.SupportsResume);
        Assert.Empty(caps.Knobs);               // honest extension point — nothing fake
    }

    [Fact]
    public void Claude_Haiku_HasNoReasoningSelector()
    {
        var caps = Runner.Claude.Capabilities("claude-haiku-4-5");
        Assert.Empty(caps.ThinkingLevels);
        Assert.Null(caps.DefaultThinkingLevel);
        Assert.False(caps.SupportsThinking);
    }

    [Fact]
    public void Codex_Gpt55_HasXHigh_DefaultsMedium_AndResumes()
    {
        var caps = Runner.Codex.Capabilities("gpt-5.5");
        Assert.Contains(CliThinkingLevels.XHigh, caps.ThinkingLevels);
        Assert.Equal(CliThinkingLevels.Medium, caps.DefaultThinkingLevel);
        Assert.True(caps.SupportsResume);
        Assert.True(caps.SupportsCleanContext);
    }

    [Fact]
    public void Gemini_NoReasoning_NoCleanContext_NoResume()
    {
        var caps = Runner.Gemini.Capabilities(model: null);
        Assert.Empty(caps.ThinkingLevels);
        Assert.False(caps.SupportsThinking);
        Assert.False(caps.SupportsCleanContext);
        Assert.False(caps.SupportsResume);     // does not handle ResumeSessionId
    }

    [Fact]
    public void Capabilities_Null_Model_IsPerCli()
    {
        // Claude needs a specific model to know its ladder → null yields none.
        Assert.Empty(Runner.Claude.Capabilities(null).ThinkingLevels);
        Assert.Null(Runner.Claude.Capabilities(null).Model);
        Assert.True(Runner.Claude.Capabilities(null).SupportsResume);   // resume is model-independent

        // Codex treats an unspecified model as base OpenAI → base ladder, default medium.
        var codex = Runner.Codex.Capabilities(null);
        Assert.Contains(CliThinkingLevels.High, codex.ThinkingLevels);
        Assert.Equal(CliThinkingLevels.Medium, codex.DefaultThinkingLevel);
    }

    [Fact]
    public void Capabilities_TrackTheModel_PerCall()
    {
        // The selector is model-specific: the SAME driver reports different ladders.
        var opus = Runner.Claude.Capabilities("claude-opus-4-8");
        var sonnet = Runner.Claude.Capabilities("claude-sonnet-4-6");
        Assert.Contains(CliThinkingLevels.Max, opus.ThinkingLevels);
        Assert.DoesNotContain(CliThinkingLevels.Max, sonnet.ThinkingLevels);
    }
}

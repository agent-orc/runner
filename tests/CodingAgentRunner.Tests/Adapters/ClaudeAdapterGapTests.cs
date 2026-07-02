using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using Xunit;

namespace CodingAgentRunner.Tests.Adapters;

/// <summary>
/// Coverage for Claude stream-json frames the per-adapter tests did not exercise:
/// rate-limit events, the extended-thinking drop, ordered text+tool emission, and
/// TodoWrite plan status normalization across all three states.
/// </summary>
public class ClaudeAdapterGapTests
{
    [Fact]
    public void RateLimitEvent_MapsToRateLimitObserved_WithAllFields()
    {
        var e = ClaudeEventAdapter.Map(
            "{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"rateLimitType\":\"five_hour\"," +
            "\"status\":\"allowed\",\"resetsAt\":1777393800,\"overageStatus\":\"allowed\",\"isUsingOverage\":false}}",
            "r").Single();
        var rl = Assert.IsType<CliRunEvent.RateLimitObserved>(e);
        // rateLimitType is normalized onto the Claude probe's window vocabulary so
        // QuotaService.Observe merges the live event into the probe's QuotaWindow.
        Assert.Equal("5-hour", rl.Window);
        Assert.Equal("allowed", rl.Status);
        Assert.Equal(1777393800L, rl.ResetsAt);
        Assert.Equal("allowed", rl.OverageStatus);
        Assert.False(rl.IsUsingOverage);
    }

    [Fact]
    public void RateLimitEvent_UsingOverageTrue_IsParsed()
    {
        var rl = Assert.IsType<CliRunEvent.RateLimitObserved>(
            ClaudeEventAdapter.Map("{\"type\":\"rate_limit_event\",\"rate_limit_info\":{\"isUsingOverage\":true}}", "r").Single());
        Assert.True(rl.IsUsingOverage);
    }

    [Fact]
    public void ThinkingBlock_IsDroppedFromTypedStream()
    {
        // Extended thinking is internal: a thinking-only assistant frame yields no events.
        Assert.Empty(ClaudeEventAdapter.Map(
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"thinking\",\"thinking\":\"let me reason\"}]}}", "r"));
    }

    [Fact]
    public void AssistantFrame_TextThenToolUse_YieldsOutputThenToolStarted_InOrder()
    {
        var events = ClaudeEventAdapter.Map(
            "{\"type\":\"assistant\",\"message\":{\"content\":[" +
            "{\"type\":\"text\",\"text\":\"reading\"}," +
            "{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"file_path\":\"/x.cs\"}}]}}", "r").ToList();
        Assert.Equal(2, events.Count);
        Assert.Equal("reading", Assert.IsType<CliRunEvent.OutputDelta>(events[0]).Text);
        var tool = Assert.IsType<CliRunEvent.ToolStarted>(events[1]);
        Assert.Equal("Read", tool.ToolName);
        Assert.Equal("/x.cs", tool.Argument);
    }

    [Fact]
    public void TodoWrite_EmitsToolStartedAndPlanUpdated_WithNormalizedStatuses()
    {
        var events = ClaudeEventAdapter.Map(
            "{\"type\":\"assistant\",\"message\":{\"content\":[{\"type\":\"tool_use\",\"name\":\"TodoWrite\",\"input\":{\"todos\":[" +
            "{\"content\":\"Write parser\",\"status\":\"in_progress\"}," +
            "{\"content\":\"Add tests\",\"status\":\"pending\"}," +
            "{\"content\":\"Ship it\",\"status\":\"completed\"}]}}]}}", "r").ToList();

        Assert.Contains(events, e => e is CliRunEvent.ToolStarted { ToolName: "TodoWrite" });
        var plan = Assert.IsType<CliRunEvent.PlanUpdated>(events.Single(e => e is CliRunEvent.PlanUpdated));
        Assert.Equal("claude/TodoWrite", plan.Source);
        Assert.Collection(plan.Items,
            i => { Assert.Equal("Write parser", i.Title); Assert.Equal("active", i.Status); },
            i => { Assert.Equal("Add tests", i.Title);   Assert.Equal("pending", i.Status); },
            i => { Assert.Equal("Ship it", i.Title);     Assert.Equal("done", i.Status); });
        // Stable id derived from the title (snapshot-independent).
        Assert.Equal(PlanItemId.From("Write parser"), plan.Items[0].Id);
    }

    [Fact]
    public void ToolResult_IsError_FlagsTheCompletion_AndTakesFirstLine()
    {
        var ok = ClaudeEventAdapter.Map(
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"is_error\":false,\"content\":\"done\"}]}}", "r").Single();
        Assert.False(Assert.IsType<CliRunEvent.ToolCompleted>(ok).IsError);

        var err = ClaudeEventAdapter.Map(
            "{\"type\":\"user\",\"message\":{\"content\":[{\"type\":\"tool_result\",\"is_error\":true,\"content\":\"boom\\nstack\"}]}}", "r").Single();
        var tc = Assert.IsType<CliRunEvent.ToolCompleted>(err);
        Assert.True(tc.IsError);
        Assert.Equal("boom", tc.FirstLine);
    }
}

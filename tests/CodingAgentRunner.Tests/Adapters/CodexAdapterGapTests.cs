using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using CodingAgentRunner.Metrics;
using Xunit;

namespace CodingAgentRunner.Tests.Adapters;

/// <summary>
/// Coverage for Codex <c>--experimental-json</c> frames the per-adapter tests did not
/// exercise: turn.failed, usage round-trip, agent_message, file_change, a started
/// command, and an update_plan completion with the <c>content</c> alias.
/// </summary>
public class CodexAdapterGapTests
{
    [Fact]
    public void Codex0166_TerraJsonl_MapsRealCompletionAndUsage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "testdata", "cli-fixtures", "codex", "0.166.0-terra.jsonl");
        var events = File.ReadAllLines(path).SelectMany(line => CodexEventAdapter.Map(line, "probe")).ToList();

        Assert.Collection(events,
            e => Assert.NotNull(Assert.IsType<CliRunEvent.SessionStarted>(e).SessionId),
            e => Assert.IsType<CliRunEvent.TurnStarted>(e),
            e => Assert.Equal("READY", Assert.IsType<CliRunEvent.OutputDelta>(e).Text),
            e =>
            {
                var usage = UsageSummaryParser.Parse(Assert.IsType<CliRunEvent.TurnCompleted>(e).UsageSummary);
                Assert.True(usage.Input > 0);
                Assert.True(usage.Output > 0);
            });
    }

    [Fact]
    public void TurnFailed_CarriesErrorMessage()
    {
        var e = CodexEventAdapter.Map("{\"type\":\"turn.failed\",\"error\":{\"message\":\"rate limit exceeded\"}}", "r").Single();
        Assert.Equal("rate limit exceeded", Assert.IsType<CliRunEvent.TurnFailed>(e).Reason);
    }

    [Fact]
    public void TurnFailed_NoMessage_FallsBackToError()
        => Assert.Equal("error",
            Assert.IsType<CliRunEvent.TurnFailed>(CodexEventAdapter.Map("{\"type\":\"turn.failed\"}", "r").Single()).Reason);

    [Fact]
    public void TurnCompleted_Usage_RoundTripsThroughUsageParser()
    {
        var e = CodexEventAdapter.Map(
            "{\"type\":\"turn.completed\",\"usage\":{\"input_tokens\":100,\"cached_input_tokens\":20,\"output_tokens\":50,\"reasoning_output_tokens\":5}}",
            "r").Single();
        var tc = Assert.IsType<CliRunEvent.TurnCompleted>(e);
        var u = UsageSummaryParser.Parse(tc.UsageSummary);   // adapter formats -> §16 parser reads
        Assert.Equal(100, u.Input);
        Assert.Equal(50, u.Output);
        Assert.Equal(20, u.Cached);
        Assert.Equal(5, u.Reasoning);
    }

    [Fact]
    public void AgentMessage_BecomesOutputDelta()
        => Assert.Equal("Hello",
            Assert.IsType<CliRunEvent.OutputDelta>(
                CodexEventAdapter.Map("{\"type\":\"item.completed\",\"item\":{\"type\":\"agent_message\",\"text\":\"Hello\"}}", "r").Single()).Text);

    [Fact]
    public void FileChange_BecomesToolCompleted_WithPath()
    {
        var tc = Assert.IsType<CliRunEvent.ToolCompleted>(
            CodexEventAdapter.Map("{\"type\":\"item.completed\",\"item\":{\"type\":\"file_change\",\"path\":\"src/x.cs\"}}", "r").Single());
        Assert.Equal("file_change", tc.ToolName);
        Assert.False(tc.IsError);
        Assert.Equal("src/x.cs", tc.FirstLine);
    }

    [Fact]
    public void StartedCommand_BecomesToolStarted_WithCommandArg()
    {
        var ts = Assert.IsType<CliRunEvent.ToolStarted>(
            CodexEventAdapter.Map("{\"type\":\"item.started\",\"item\":{\"type\":\"command_execution\",\"command\":\"git status\"}}", "r").Single());
        Assert.Equal("command_execution", ts.ToolName);
        Assert.Equal("git status", ts.Argument);
    }

    [Fact]
    public void UpdatePlanCompleted_EmitsPlanUpdated_NormalizedStatuses_AndContentAlias()
    {
        var events = CodexEventAdapter.Map(
            "{\"type\":\"item.completed\",\"item\":{\"type\":\"update_plan\",\"plan\":[" +
            "{\"step\":\"Survey\",\"status\":\"completed\"}," +
            "{\"content\":\"Patch\",\"status\":\"in_progress\"}]}}", "r").ToList();
        var plan = Assert.IsType<CliRunEvent.PlanUpdated>(events.Single(x => x is CliRunEvent.PlanUpdated));
        Assert.Equal("codex/update_plan", plan.Source);
        Assert.Collection(plan.Items,
            i => { Assert.Equal("Survey", i.Title); Assert.Equal("done", i.Status); },
            i => { Assert.Equal("Patch", i.Title); Assert.Equal("active", i.Status); });   // content alias for step
    }
}

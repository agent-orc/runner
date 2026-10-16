using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using CodingAgentRunner.Metrics;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Tests.Adapters;

public class AntigravityEventAdapterTests
{
    private const string Id = "fa6f65a6-1111-2222-3333-444444444444";

    [Fact]
    public void InitAndStepUpdates_MapSessionTextAndTools()
    {
        var init = Map("""{"event":"init","conversation_id":"fa6f65a6-1111-2222-3333-444444444444","init":{"model":"gemini-4-argon-low","cwd":"C:\\work","tools":["ask_permission","run_command"]}}""");
        var session = Assert.IsType<CliRunEvent.SessionStarted>(Assert.Single(init));
        Assert.Equal(Id, session.SessionId);
        Assert.Equal("gemini-4-argon-low", session.Model);
        Assert.Equal("C:\\work", session.Cwd);

        Assert.Empty(Map("""{"event":"step_update","step_update":{"step_index":0,"state":"DONE","step_type":"user_input"}}"""));
        Assert.Empty(Map("""{"event":"step_update","step_update":{"step_index":1,"state":"DONE","step_type":"agent_response","usage":{"input_tokens":11805,"output_tokens":599}}}"""));
        var started = Assert.IsType<CliRunEvent.ToolStarted>(Assert.Single(Map("""{"event":"step_update","step_update":{"step_index":2,"state":"ACTIVE","step_type":"tool","tool_name":"write_to_file","tool_info":{"name":"write_to_file","parameters":{"TargetFile":"C:\\work\\fizzbuzz.js"}}}}""")));
        Assert.Equal("write_to_file", started.ToolName);
        Assert.Equal("C:\\work\\fizzbuzz.js", started.Argument);
        var ended = Assert.IsType<CliRunEvent.ToolCompleted>(Assert.Single(Map("""{"event":"step_update","step_update":{"step_index":2,"state":"DONE","step_type":"tool","tool_name":"write_to_file","tool_info":{"output":"saved\nmore"}}}""")));
        Assert.Equal("saved", ended.FirstLine);
        Assert.False(ended.IsError);
        var command = Assert.IsType<CliRunEvent.ToolStarted>(Assert.Single(Map("""{"event":"step_update","step_update":{"step_index":5,"state":"ACTIVE","step_type":"tool","tool_name":"run_command","tool_info":{"parameters":{"CommandLine":"node --test"}}}}""")));
        Assert.Equal("node --test", command.Argument);
        var delta = Assert.IsType<CliRunEvent.OutputDelta>(Assert.Single(Map("""{"event":"step_update","step_update":{"step_index":6,"state":"ACTIVE","step_type":"agent_response","text_delta":"Created fizzbuzz"}}""")));
        Assert.Equal("Created fizzbuzz", delta.Text);
    }

    [Fact]
    public void Result_UsesStatusAndAllUsageFields()
    {
        var complete = Assert.IsType<CliRunEvent.TurnCompleted>(Assert.Single(Map("""{"event":"result","result":{"conversation_id":"fa6f65a6-1111-2222-3333-444444444444","status":"SUCCESS","response":"Created ...","structured_output":{"wrong":true},"usage":{"input_tokens":17158,"output_tokens":1022,"thinking_tokens":17,"cache_read_tokens":20487,"total_tokens":18180}}}""")));
        Assert.Equal(new UsageTokens(17158, 1022, 20487, 17), UsageSummaryParser.Parse(complete.UsageSummary));
        var failure = Assert.IsType<CliRunEvent.TurnFailed>(Assert.Single(Map("""{"event":"result","result":{"status":"ERROR","error":"bad model"}}""")));
        Assert.Equal("bad model", failure.Reason);
    }

    [Fact]
    public void DenialOnExitZero_IsTypedFailureAndStderrDiagnostic()
    {
        var failure = Assert.IsType<CliRunEvent.TurnFailed>(Assert.Single(Map("""{"event":"result","result":{"status":"SUCCESS","response":"","denied_actions":[{"action":"read_url","display_name":"ReadUrlContent"}]}}""")));
        Assert.Contains("permission denied", failure.Reason);
        Assert.Equal("read_url", Assert.Single(failure.DeniedActions).Action);
        Assert.Equal("ReadUrlContent", failure.DeniedActions[0].DisplayName);
        var diagnostic = Assert.IsType<CliRunEvent.Diagnostic>(Assert.Single(AntigravityEventAdapter.Map("jetski: no output produced — a tool required the read_url permission, so it was auto-denied.", "run", CliStreamKind.Stderr)));
        Assert.Equal("permission_denied", diagnostic.Code);
    }

    private static CliRunEvent[] Map(string line) => AntigravityEventAdapter.Map(line, "run").ToArray();
}

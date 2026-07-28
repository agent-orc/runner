using CodingAgentRunner.Adapters;
using CodingAgentRunner.Delegation;
using CodingAgentRunner.Events;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Delegation;

/// <summary>
/// Delegation only pays for itself if the ledger can see it. These pin that the
/// adapters survive the frames a delegated subtask produces and name the agent that
/// ran, instead of reporting an anonymous tool call.
/// </summary>
public class DelegationStreamTests
{
    // Claude Code has shipped the delegation tool under both names — `Agent` in
    // 2.1.220, `Task` in earlier versions and still in the init frame's tool list. The
    // adapter keys off the frame's `subagent_type` instead of the tool name, so both
    // report which cheap agent ran.
    [Theory]
    [InlineData("Agent")]
    [InlineData("Task")]
    public void Claude_SubagentToolUse_NamesTheSubagentThatWasSpawned(string toolName)
    {
        const string template = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"TOOL_NAME","input":{"subagent_type":"mechanical","description":"Grep FIXME in src and tests","run_in_background":false,"prompt":"In the repository at ..."}}]}}
            """;
        var frame = template.Replace("TOOL_NAME", toolName);

        var started = Assert.IsType<CliRunEvent.ToolStarted>(Assert.Single(ClaudeEventAdapter.Map(frame, "run-1")));
        Assert.Equal(toolName, started.ToolName);
        Assert.Equal("mechanical", started.Argument);
    }

    [Fact]
    public void Claude_SubagentFramesCarryingParentToolUseId_StillParse()
    {
        // Frames produced *inside* a delegated subtask carry parent_tool_use_id. They
        // are ordinary assistant/user frames otherwise, and must not degrade to Unknown.
        const string assistant = """
            {"type":"assistant","parent_tool_use_id":"toolu_01","message":{"content":[{"type":"text","text":"found 3 call sites"}]}}
            """;
        const string toolResult = """
            {"type":"user","parent_tool_use_id":"toolu_01","message":{"content":[{"type":"tool_result","is_error":false,"content":"src/a.cs\nsrc/b.cs"}]}}
            """;

        var delta = Assert.IsType<CliRunEvent.OutputDelta>(Assert.Single(ClaudeEventAdapter.Map(assistant, "run-1")));
        Assert.Equal("found 3 call sites", delta.Text);

        var completed = Assert.IsType<CliRunEvent.ToolCompleted>(Assert.Single(ClaudeEventAdapter.Map(toolResult, "run-1")));
        Assert.False(completed.IsError);
        Assert.Equal("src/a.cs", completed.FirstLine);
    }

    [Fact]
    public void Claude_SubagentToolResult_IsATypedToolCompletion()
    {
        const string frame = """
            {"type":"user","message":{"content":[{"type":"tool_result","is_error":false,"content":[{"type":"text","text":"mechanical: 3 matches"}]}]}}
            """;

        var completed = Assert.IsType<CliRunEvent.ToolCompleted>(Assert.Single(ClaudeEventAdapter.Map(frame, "run-1")));
        Assert.Equal("mechanical: 3 matches", completed.FirstLine);
    }

    [Fact]
    public void Codex_CollabToolCallFrames_MapToToolEventsWithTheCollabToolName()
    {
        // Verbatim shape from codex-cli 0.145.0 `codex exec --experimental-json` when a
        // prompt asks for a subagent. Note the empty receiver_thread_ids: exec accepts
        // the call but starts no child thread. See docs/delegation.md.
        const string started = """
            {"type":"item.started","item":{"id":"item_1","type":"collab_tool_call","tool":"wait","sender_thread_id":"019fa935-76b4-7e43-bc30-5df4c14710f4","receiver_thread_ids":[],"prompt":null,"agents_states":{},"status":"in_progress"}}
            """;
        const string completed = """
            {"type":"item.completed","item":{"id":"item_1","type":"collab_tool_call","tool":"wait","sender_thread_id":"019fa935-76b4-7e43-bc30-5df4c14710f4","receiver_thread_ids":[],"prompt":null,"agents_states":{},"status":"completed"}}
            """;

        var toolStarted = Assert.IsType<CliRunEvent.ToolStarted>(Assert.Single(CodexEventAdapter.Map(started, "run-1")));
        Assert.Equal("collab_tool_call", toolStarted.ToolName);
        Assert.Equal("wait", toolStarted.Argument);

        var toolCompleted = Assert.IsType<CliRunEvent.ToolCompleted>(Assert.Single(CodexEventAdapter.Map(completed, "run-1")));
        Assert.Equal("collab_tool_call", toolCompleted.ToolName);
        Assert.False(toolCompleted.IsError);
    }

    [Fact]
    public void Codex_AgentSpawnItem_MapsToATypedToolEvent()
    {
        const string frame = """
            {"type":"item.completed","item":{"id":"item_2","type":"agent_spawn","agent_name":"mechanical","status":"completed"}}
            """;

        var completed = Assert.IsType<CliRunEvent.ToolCompleted>(Assert.Single(CodexEventAdapter.Map(frame, "run-1")));
        Assert.Equal("agent_spawn", completed.ToolName);
        Assert.Equal("mechanical", completed.FirstLine);
    }

    [Fact]
    public void Claude_NonDelegationToolArguments_AreUnchanged()
    {
        // The Task keys were appended after the existing ones, so a tool that has both
        // a file_path and a description still reports the path.
        const string frame = """
            {"type":"assistant","message":{"content":[{"type":"tool_use","name":"Edit","input":{"file_path":"src/a.cs","description":"tidy"}}]}}
            """;

        var started = Assert.IsType<CliRunEvent.ToolStarted>(Assert.Single(ClaudeEventAdapter.Map(frame, "run-1")));
        Assert.Equal("src/a.cs", started.Argument);
    }

    [Fact]
    public void Codex_NonDelegationItemArguments_AreUnchanged()
    {
        const string frame = """
            {"type":"item.completed","item":{"id":"item_3","type":"command_call","command":"dotnet build","tool":"shell"}}
            """;

        var completed = Assert.IsType<CliRunEvent.ToolCompleted>(Assert.Single(CodexEventAdapter.Map(frame, "run-1")));
        Assert.Equal("dotnet build", completed.FirstLine);
    }

    [Fact]
    public void ClaudeDescriptorCarriesTheAgentConvention_CodexToo_OthersDoNot()
    {
        var runner = new CliRunner();
        Assert.Contains(CliTypes.Claude, runner.SupportedCliTypes);
        // The convention lives on the descriptor, so a CLI without one (Antigravity,
        // Gemini) simply materializes nothing.
        Assert.Equal(".claude/agents", SubagentRenderers.Claude.DirectoryRelativePath);
        Assert.Equal(".codex/agents", SubagentRenderers.Codex.DirectoryRelativePath);
    }
}

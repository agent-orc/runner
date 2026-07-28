using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Execution;
using Xunit;

namespace CodingAgentRunner.Tests.Drivers;

public class CliDriverArgvTests
{
    private static CliRunRequest Req(string? model = null, string? thinking = null, string? perm = null,
        string? session = null, bool resume = false) => new()
    {
        RunId = "run-1",
        Prompt = "line one\nline two",   // multi-line on purpose
        WorkingDirectory = Path.GetTempPath(),
        Model = model,
        ThinkingLevel = thinking,
        PermissionMode = perm,
        ResumeSessionId = resume ? session : null,   // the id alone IS the resume signal
    };

    // Build the launch spec exactly as a real run would (model + thinking normalized),
    // straight from the built-in descriptor through the engine's test hook.
    private static LaunchSpec Launch(CliDescriptor descriptor, CliRunRequest req, CliOptions? options = null)
        => new CliRunEngine(descriptor, options).BuildLaunchForTest(req);

    [Fact]
    public void Claude_DefaultArgvTransport_PreservesExistingLaunchExactly()
    {
        var launch = Launch(BuiltInDescriptors.Claude, Req());

        Assert.Equal(ClaudePromptTransport.Argv, new CliOptions().ClaudePromptTransport);
        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions", "line one\nline two"],
            launch.Argv);
        Assert.Null(launch.StdinPayload);
    }

    [Fact]
    public void Claude_StdinTransport_RemovesPromptArgumentAndSetsPayload()
    {
        var launch = Launch(
            BuiltInDescriptors.Claude,
            Req(),
            new CliOptions { ClaudePromptTransport = ClaudePromptTransport.Stdin });

        Assert.Equal(
            ["-p", "--output-format", "stream-json", "--verbose", "--dangerously-skip-permissions"],
            launch.Argv);
        Assert.DoesNotContain("line one\nline two", launch.Argv);
        Assert.Equal("line one\nline two", launch.StdinPayload);
    }

    [Fact]
    public void Claude_PutsPromptLast_UsesStreamJson_AndYoloByDefault()
    {
        var args = Launch(BuiltInDescriptors.Claude, Req(model: "claude-opus-4-8", thinking: "xhigh")).Argv;

        Assert.Equal("-p", args[0]);
        Assert.Contains("--output-format", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--verbose", args);
        Assert.Contains("--model", args);
        Assert.Contains("claude-opus-4-8", args);
        // reasoning flag rendered for an xhigh-capable model
        Assert.Contains("--effort", args);
        Assert.Contains("xhigh", args);
        // default permission == YOLO
        Assert.Contains("--dangerously-skip-permissions", args);
        // The multi-line prompt is the LAST positional arg (never via cmd.exe).
        Assert.Equal("line one\nline two", args[^1]);
    }

    [Fact]
    public void Claude_Resume_AddsDashR()
    {
        var args = Launch(BuiltInDescriptors.Claude, Req(session: "abc-123", resume: true)).Argv;
        Assert.Contains("-r", args);
        Assert.Contains("abc-123", args);
    }

    [Fact]
    public void Codex_UsesExecExperimentalJson_StdinDash_AndSandbox()
    {
        var launch = Launch(BuiltInDescriptors.Codex, Req(model: "gpt-5.5", thinking: "high"));
        var args = launch.Argv;

        Assert.Equal("exec", args[0]);
        Assert.Contains("--experimental-json", args);
        Assert.Contains("--sandbox", args);
        Assert.Contains("danger-full-access", args);     // YOLO default
        Assert.Contains("-m", args);
        Assert.Contains("gpt-5.5", args);
        Assert.Contains("-c", args);                     // reasoning effort config
        Assert.Contains("model_reasoning_effort=\"high\"", args);
        Assert.Equal("-", args[^1]);                     // prompt via stdin

        // ...and the prompt is the stdin payload, not an argv.
        Assert.Equal("line one\nline two", launch.StdinPayload);
    }

    [Fact]
    public void Codex_Resume_OnlyForAUuidSession()
    {
        Assert.DoesNotContain("resume", Launch(BuiltInDescriptors.Codex, Req(session: "not-a-uuid", resume: true)).Argv);

        var withUuid = Launch(BuiltInDescriptors.Codex,
            Req(session: "12345678-1234-1234-1234-123456789abc", resume: true)).Argv;
        Assert.Contains("resume", withUuid);
    }

    [Fact]
    public void Codex_Tuning_BecomesConfigOverrides()
    {
        var args = Launch(BuiltInDescriptors.Codex, new CliRunRequest
        {
            RunId = "r",
            Prompt = "p",
            WorkingDirectory = Path.GetTempPath(),
            Tuning = new Dictionary<string, string> { ["model_reasoning_summary"] = "concise" },
        }).Argv;
        Assert.Contains("-c", args);
        Assert.Contains("model_reasoning_summary=concise", args);
    }

    [Fact]
    public void Gemini_UsesStreamJson_AndAlwaysSkipsTrust()
    {
        var args = Launch(BuiltInDescriptors.Gemini, Req()).Argv;
        Assert.Contains("-o", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--skip-trust", args);   // never blocks on the trust modal
    }

    [Fact]
    public void Antigravity_NewConversationWithModel_AndResumeViaSendMessage()
    {
        // New conversation: new-conversation --model=<tier> "<prompt>"
        var fresh = Launch(BuiltInDescriptors.Antigravity, Req(model: "gemini-pro")).Argv;
        Assert.Equal("new-conversation", fresh[0]);
        Assert.Contains("--model=pro", fresh);              // pro tier mapped
        Assert.Equal("line one\nline two", fresh[^1]);      // prompt is the last positional

        // Resume: send-message <uuid> "<prompt>"
        var resumed = Launch(BuiltInDescriptors.Antigravity,
            Req(session: "12345678-1234-1234-1234-123456789abc", resume: true)).Argv;
        Assert.Equal("send-message", resumed[0]);
        Assert.Contains("12345678-1234-1234-1234-123456789abc", resumed);
        Assert.DoesNotContain("new-conversation", resumed);
    }
}

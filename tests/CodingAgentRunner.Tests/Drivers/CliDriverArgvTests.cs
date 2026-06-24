using CodingAgentRunner.Drivers;
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

    [Fact]
    public void Claude_PutsPromptLast_UsesStreamJson_AndYoloByDefault()
    {
        var psi = new ClaudeDriver().BuildStartInfoForTest(Req(model: "claude-opus-4-8", thinking: "xhigh"));
        var args = psi.ArgumentList;

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
        var psi = new ClaudeDriver().BuildStartInfoForTest(Req(session: "abc-123", resume: true));
        Assert.Contains("-r", psi.ArgumentList);
        Assert.Contains("abc-123", psi.ArgumentList);
    }

    [Fact]
    public void Codex_UsesExecExperimentalJson_StdinDash_AndSandbox()
    {
        var driver = new CodexDriver();
        var psi = driver.BuildStartInfoForTest(Req(model: "gpt-5.5", thinking: "high"));
        var args = psi.ArgumentList;

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
        Assert.Equal("line one\nline two", driver.BuildPromptStdinPayloadForTest(Req(model: "gpt-5.5")));
    }

    [Fact]
    public void Codex_Resume_OnlyForAUuidSession()
    {
        var psi = new CodexDriver().BuildStartInfoForTest(Req(session: "not-a-uuid", resume: true));
        Assert.DoesNotContain("resume", psi.ArgumentList);

        var withUuid = new CodexDriver().BuildStartInfoForTest(
            Req(session: "12345678-1234-1234-1234-123456789abc", resume: true));
        Assert.Contains("resume", withUuid.ArgumentList);
    }

    [Fact]
    public void Codex_Tuning_BecomesConfigOverrides()
    {
        var psi = new CodexDriver().BuildStartInfoForTest(new CliRunRequest
        {
            RunId = "r",
            Prompt = "p",
            WorkingDirectory = Path.GetTempPath(),
            Tuning = new Dictionary<string, string> { ["model_reasoning_summary"] = "concise" },
        });
        var args = psi.ArgumentList;
        Assert.Contains("-c", args);
        Assert.Contains("model_reasoning_summary=concise", args);
    }

    [Fact]
    public void Gemini_UsesStreamJson_AndAlwaysSkipsTrust()
    {
        var psi = new GeminiDriver().BuildStartInfoForTest(Req());
        var args = psi.ArgumentList;
        Assert.Contains("-o", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--skip-trust", args);   // never blocks on the trust modal
    }

    [Fact]
    public void Antigravity_NewConversationWithModel_AndResumeViaSendMessage()
    {
        // New conversation: new-conversation --model=<tier> "<prompt>"
        var fresh = new AntigravityDriver().BuildStartInfoForTest(Req(model: "gemini-pro"));
        Assert.Equal("new-conversation", fresh.ArgumentList[0]);
        Assert.Contains("--model=pro", fresh.ArgumentList);              // pro tier mapped
        Assert.Equal("line one\nline two", fresh.ArgumentList[^1]);      // prompt is the last positional

        // Resume: send-message <uuid> "<prompt>"
        var resumed = new AntigravityDriver().BuildStartInfoForTest(
            Req(session: "12345678-1234-1234-1234-123456789abc", resume: true));
        Assert.Equal("send-message", resumed.ArgumentList[0]);
        Assert.Contains("12345678-1234-1234-1234-123456789abc", resumed.ArgumentList);
        Assert.DoesNotContain("new-conversation", resumed.ArgumentList);
    }

}

using CodingAgentRunner.Backends;
using CodingAgentRunner.Execution;
using Xunit;

namespace CodingAgentRunner.Tests.Backends;

public class CliBackendArgvTests
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
        SessionName = session,
        ResumeSession = resume,
    };

    [Fact]
    public void Claude_PutsPromptLast_UsesStreamJson_AndYoloByDefault()
    {
        var psi = new ClaudeBackend().BuildStartInfoForTest(Req(model: "claude-opus-4-8", thinking: "xhigh"));
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
        var psi = new ClaudeBackend().BuildStartInfoForTest(Req(session: "abc-123", resume: true));
        Assert.Contains("-r", psi.ArgumentList);
        Assert.Contains("abc-123", psi.ArgumentList);
    }

    [Fact]
    public void Codex_UsesExecExperimentalJson_StdinDash_AndSandbox()
    {
        var backend = new CodexBackend();
        var psi = backend.BuildStartInfoForTest(Req(model: "gpt-5.5", thinking: "high"));
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
        Assert.Equal("line one\nline two", backend.BuildPromptStdinPayloadForTest(Req(model: "gpt-5.5")));
    }

    [Fact]
    public void Codex_Resume_OnlyForAUuidSession()
    {
        var psi = new CodexBackend().BuildStartInfoForTest(Req(session: "not-a-uuid", resume: true));
        Assert.DoesNotContain("resume", psi.ArgumentList);

        var withUuid = new CodexBackend().BuildStartInfoForTest(
            Req(session: "12345678-1234-1234-1234-123456789abc", resume: true));
        Assert.Contains("resume", withUuid.ArgumentList);
    }

    [Fact]
    public void Gemini_UsesStreamJson_AndAlwaysSkipsTrust()
    {
        var psi = new GeminiBackend().BuildStartInfoForTest(Req());
        var args = psi.ArgumentList;
        Assert.Contains("-o", args);
        Assert.Contains("stream-json", args);
        Assert.Contains("--skip-trust", args);   // never blocks on the trust modal
    }

    [Fact]
    public void Copilot_AllowAll_AndPrompt()
    {
        var psi = new CopilotBackend().BuildStartInfoForTest(Req());
        var args = psi.ArgumentList;
        Assert.Equal("-p", args[0]);
        Assert.Contains("--allow-all", args);
    }
}

using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Model;

public class CliValueTypesTests
{
    [Theory]
    [InlineData("claude", "claude")]
    [InlineData("CLAUDE", "claude")]
    [InlineData("codex", "codex")]
    [InlineData(null, "claude")]       // unknown / empty -> claude
    [InlineData("nonsense", "claude")]
    public void CliTypes_NormalizeFallsBackToClaude(string? input, string expected)
    {
        Assert.Equal(expected, CliTypes.Normalize(input));
    }

    [Fact]
    public void CliTypes_HumanIsNotASelectableDriver()
    {
        Assert.False(CliTypes.IsValid("human"));
        Assert.DoesNotContain(CliTypes.Human, CliTypes.All);
    }

    [Theory]
    [InlineData("claude", CliPermissionModes.Yolo, "--dangerously-skip-permissions")]
    [InlineData("codex", CliPermissionModes.Yolo, "danger-full-access")]
    [InlineData("gemini", CliPermissionModes.Yolo, "--skip-trust")]
    public void CliPermissionFlags_YoloMapsPerCli(string cli, string mode, string mustContain)
    {
        Assert.Contains(mustContain, CliPermissionFlags.For(cli, mode));
    }

    [Fact]
    public void CliPermissionFlags_GeminiAlwaysSkipsTrust()
    {
        // The folder-trust modal must never be reachable in any mode.
        Assert.Contains("--skip-trust", CliPermissionFlags.For("gemini", CliPermissionModes.Yolo));
        Assert.Contains("--skip-trust", CliPermissionFlags.For("gemini", CliPermissionModes.ReadOnly));
        Assert.Contains("--skip-trust", CliPermissionFlags.For("gemini", CliPermissionModes.Custom));
    }

    [Fact]
    public void CliPermissionFlags_CustomInjectsNothingForClaude()
    {
        Assert.Empty(CliPermissionFlags.For("claude", CliPermissionModes.Custom));
    }

    [Theory]
    [InlineData("claude", true)]
    [InlineData("codex", true)]
    [InlineData("gemini", false)]
    public void CliContextModes_SupportsCleanOnlyForRedirectableClis(string cli, bool expected)
    {
        Assert.Equal(expected, CliContextModes.SupportsClean(cli));
    }

    [Fact]
    public void CliContextModes_DefaultsToClean()
    {
        Assert.Equal(CliContextModes.Clean, CliContextModes.Normalize(null));
        Assert.Equal(CliContextModes.Clean, CliContextModes.Normalize("garbage"));
    }

    [Fact]
    public void CliThinkingLevels_Opus48OffersXHighAndMax_HaikuOffersNone()
    {
        var opus = CliThinkingLevels.For("claude", "claude-opus-4-8");
        Assert.Contains(CliThinkingLevels.XHigh, opus);
        Assert.Contains(CliThinkingLevels.Max, opus);

        Assert.Empty(CliThinkingLevels.For("claude", "claude-haiku-4-5"));
    }

    [Fact]
    public void CliThinkingLevels_CodexXHighOnlyForGpt55Plus()
    {
        Assert.Contains(CliThinkingLevels.XHigh, CliThinkingLevels.For("codex", "gpt-5.5"));
        Assert.DoesNotContain(CliThinkingLevels.XHigh, CliThinkingLevels.For("codex", "gpt-5-codex"));
        // A foreign model on the codex CLI has no selector at all.
        Assert.Empty(CliThinkingLevels.For("codex", "claude-opus-4-8"));
    }

    [Fact]
    public void CliThinkingLevels_NormalizeFallsBackToDefaultForUnknownRequest()
    {
        // Codex default is medium; an unsupported request falls back to it.
        Assert.Equal(CliThinkingLevels.Medium, CliThinkingLevels.Normalize("codex", "gpt-5-codex", "ludicrous"));
        // No selector -> null regardless of request.
        Assert.Null(CliThinkingLevels.Normalize("claude", "claude-haiku-4-5", "high"));
    }

    [Theory]
    [InlineData(null, CliPermissionModes.Yolo)]
    [InlineData("", CliPermissionModes.Yolo)]
    [InlineData("   ", CliPermissionModes.Yolo)]
    [InlineData("YOLO", CliPermissionModes.Yolo)]
    [InlineData(" read-only ", CliPermissionModes.ReadOnly)]
    [InlineData("Workspace-Write", CliPermissionModes.WorkspaceWrite)]
    [InlineData("custom", CliPermissionModes.Custom)]
    [InlineData("nonsense", CliPermissionModes.Yolo)]   // unknown → Yolo (never an interactive hang)
    public void CliPermissionModes_NormalizeMatrix(string? input, string expected)
        => Assert.Equal(expected, CliPermissionModes.Normalize(input));

    [Fact]
    public void PermissionAndContext_Modes_RoundTripThroughNormalize()
    {
        foreach (var m in CliPermissionModes.All)
            Assert.Equal(m, CliPermissionModes.Normalize(m));
        foreach (var c in CliContextModes.All)
            Assert.Equal(c, CliContextModes.Normalize(c));
    }

    [Theory]
    [InlineData(null, "claude")]                   // unknown / empty -> claude
    [InlineData("GEMINI", "gemini")]               // case-insensitive
    [InlineData("  gemini  ", "claude")]           // NOT trimmed (unlike CliPermissionModes) → unknown → claude
    [InlineData("human", "claude")]                // the sentinel is not a selectable driver
    public void CliTypes_Normalize_IsCaseInsensitive_ButDoesNotTrim(string? input, string expected)
        => Assert.Equal(expected, CliTypes.Normalize(input));
}

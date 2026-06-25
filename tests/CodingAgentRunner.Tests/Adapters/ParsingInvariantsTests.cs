using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using Xunit;

namespace CodingAgentRunner.Tests.Adapters;

/// <summary>
/// Cross-CLI parsing invariants + gap coverage for the three frame adapters
/// (Claude / Codex / Gemini). The adapters must be pure + total: any line —
/// malformed JSON, a non-object payload, an unknown frame type — yields events
/// (possibly just <see cref="CliRunEvent.Unknown"/>) and NEVER throws. These run
/// every adapter against a shared adversarial battery, plus a few documented
/// per-CLI cases the existing per-adapter tests did not cover.
/// </summary>
public class ParsingInvariantsTests
{
    private static readonly (string Cli, Func<string, string, IEnumerable<CliRunEvent>> Map)[] Adapters =
    [
        ("claude", ClaudeEventAdapter.Map),
        ("codex",  CodexEventAdapter.Map),
        ("gemini", GeminiEventAdapter.Map),
    ];

    /// <summary>Lines that are not a JSON object an adapter can read → zero events (never an exception).</summary>
    public static IEnumerable<object[]> NonFrameLines =>
        new[] { "", "   ", "not json at all", "{ broken json", "[]", "[1,2,3]", "42", "true", "null", "\"a string\"" }
            .Select(s => new object[] { s });

    /// <summary>Valid JSON objects whose type the adapter does not recognise — must not throw, must tag the run id.</summary>
    public static IEnumerable<object[]> UnknownObjectLines =>
        new[]
        {
            "{}",
            "{\"type\":123}",
            "{\"type\":null}",
            "{\"type\":true}",
            "{\"type\":\"totally_unknown_frame_xyz\"}",
            "{\"nested\":{\"type\":\"assistant\"}}",
        }.Select(s => new object[] { s });

    [Theory]
    [MemberData(nameof(NonFrameLines))]
    public void EveryAdapter_NeverThrows_AndYieldsNothing_OnNonFrameInput(string line)
    {
        foreach (var (cli, map) in Adapters)
        {
            var events = AssertNoThrow(map, line);
            Assert.True(events.Count == 0, $"{cli} yielded {events.Count} events for non-frame input '{line}'");
        }
    }

    [Theory]
    [MemberData(nameof(UnknownObjectLines))]
    public void EveryAdapter_NeverThrows_OnUnknownObject(string line)
    {
        foreach (var (_, map) in Adapters)
            AssertNoThrow(map, line);
    }

    [Theory]
    [MemberData(nameof(UnknownObjectLines))]
    public void EveryAdapter_TagsEveryEventWithTheRunId(string line)
    {
        foreach (var (_, map) in Adapters)
            Assert.All(map(line, "run-7").ToList(), e => Assert.Equal("run-7", e.RunId));
    }

    [Fact]
    public void EveryAdapter_MapsAnUnknownTypeString_ToUnknown_Capped200()
    {
        var huge = "{\"type\":\"weird_frame\",\"blob\":\"" + new string('x', 4000) + "\"}";
        foreach (var (cli, map) in Adapters)
        {
            var unknowns = map(huge, "r").OfType<CliRunEvent.Unknown>().ToList();
            Assert.True(unknowns.Count >= 1, $"{cli} did not map an unknown frame type to Unknown");
            Assert.All(unknowns, u => Assert.True(u.Sample.Length <= 200, $"{cli} Unknown.Sample was {u.Sample.Length} chars"));
        }
    }

    // ── Documented per-CLI gap coverage ─────────────────────────────────

    [Fact]
    public void Codex_SessionMetaLegacy_MapsToSessionStarted()
    {
        var e = CodexEventAdapter.Map("{\"type\":\"session_meta\",\"session_id\":\"abc-123\"}", "r").Single();
        Assert.Equal("abc-123", Assert.IsType<CliRunEvent.SessionStarted>(e).SessionId);
    }

    [Fact]
    public void Gemini_ToolResult_FlagsErrorByPresenceOfErrorField()
    {
        var ok = GeminiEventAdapter.Map("{\"type\":\"tool_result\",\"name\":\"read_file\",\"output\":\"line1\\nline2\"}", "r").Single();
        Assert.False(Assert.IsType<CliRunEvent.ToolCompleted>(ok).IsError);

        var err = GeminiEventAdapter.Map("{\"type\":\"tool_result\",\"name\":\"read_file\",\"error\":\"boom\"}", "r").Single();
        Assert.True(Assert.IsType<CliRunEvent.ToolCompleted>(err).IsError);
    }

    [Fact]
    public void Gemini_UserMessageIgnored_AssistantMessageIsOutput()
    {
        Assert.Empty(GeminiEventAdapter.Map("{\"type\":\"message\",\"role\":\"user\",\"content\":\"hi\"}", "r"));
        var a = GeminiEventAdapter.Map("{\"type\":\"message\",\"role\":\"assistant\",\"content\":\"hello\"}", "r").Single();
        Assert.Equal("hello", Assert.IsType<CliRunEvent.OutputDelta>(a).Text);
    }

    [Fact]
    public void Gemini_ResultStatus_SuccessIsTurnCompleted_OtherwiseTurnFailed()
    {
        Assert.IsType<CliRunEvent.TurnCompleted>(
            GeminiEventAdapter.Map("{\"type\":\"result\",\"status\":\"success\",\"stats\":{\"input_tokens\":10,\"output_tokens\":5}}", "r").Single());
        Assert.IsType<CliRunEvent.TurnFailed>(
            GeminiEventAdapter.Map("{\"type\":\"result\",\"status\":\"error\"}", "r").Single());
    }

    private static List<CliRunEvent> AssertNoThrow(Func<string, string, IEnumerable<CliRunEvent>> map, string line)
    {
        List<CliRunEvent>? events = null;
        Assert.Null(Record.Exception(() => events = map(line, "run-1").ToList()));
        return events!;
    }
}

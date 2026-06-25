using CodingAgentRunner.Events;
using CodingAgentRunner.Metrics;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Metrics;

public class RunMetricsTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    // Claude reports cache_read=; Codex/Gemini report cached= — both fold to Cached.
    [InlineData("input=1234 output=567 cache_read=89", 1234, 567, 89, 0)]
    [InlineData("input=22267 cached=6528 output=10 reasoning=0", 22267, 10, 6528, 0)]
    [InlineData("input=1000 output=500 cached=100 tool_calls=2", 1000, 500, 100, 0)]   // tool_calls ignored
    [InlineData("input=5 reasoning=42 output=7", 5, 7, 0, 42)]
    [InlineData(null, 0, 0, 0, 0)]
    [InlineData("", 0, 0, 0, 0)]
    [InlineData("garbage with no equals", 0, 0, 0, 0)]
    [InlineData("input=notanumber output=3", 0, 3, 0, 0)]
    public void UsageSummaryParser_HandlesPerCliVocabulary(string? summary, long input, long output, long cached, long reasoning)
    {
        var u = UsageSummaryParser.Parse(summary);
        Assert.Equal(input, u.Input);
        Assert.Equal(output, u.Output);
        Assert.Equal(cached, u.Cached);
        Assert.Equal(reasoning, u.Reasoning);
    }

    [Fact]
    public void Recorder_DerivesTtfo_Ttsi_TurnWallClock_TokensPerSec_FromTheStream()
    {
        var rec = new RunMetricsRecorder();
        rec.Observe(new CliRunEvent.RunStarted(123, CliTypes.Codex, "gpt-5.5") { RunId = "r", ObservedAt = T0 });
        rec.Observe(new CliRunEvent.SessionStarted("sid") { RunId = "r", ObservedAt = T0.AddSeconds(1) });
        rec.Observe(new CliRunEvent.TurnStarted { RunId = "r", ObservedAt = T0.AddSeconds(2) });
        rec.Observe(new CliRunEvent.OutputDelta("hi") { RunId = "r", ObservedAt = T0.AddSeconds(3) });
        rec.Observe(new CliRunEvent.TurnCompleted("input=1000 cached=200 output=500 reasoning=10") { RunId = "r", ObservedAt = T0.AddSeconds(12) });
        rec.Observe(new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 12.5) { RunId = "r", ObservedAt = T0.AddSeconds(13) });

        var m = rec.Build();

        Assert.Equal("r", m.RunId);
        Assert.Equal(CliTypes.Codex, m.CliType);
        Assert.Equal("gpt-5.5", m.Model);
        Assert.Equal(1000d, m.TimeToSessionIdMs!.Value);       // start → SessionStarted = 1s
        Assert.Equal(3000d, m.TimeToFirstOutputMs!.Value);     // start → first OutputDelta = 3s
        Assert.Equal(12500d, m.TotalDurationMs!.Value);        // RunEnded.Duration 12.5s
        Assert.Equal(1, m.TurnCount);
        Assert.Equal("Completed", m.Outcome);

        // Turn wall-clock = TurnStarted(2s) → TurnCompleted(12s) = 10s; tokens/sec = 500/10 = 50.
        var turn = Assert.Single(m.Turns);
        Assert.Equal(10000d, turn.WallClockMs);
        Assert.Equal(500, turn.OutputTokens);
        Assert.Equal(200, turn.CachedInputTokens);
        Assert.Equal(10, turn.ReasoningOutputTokens);
        Assert.Equal(50d, turn.OutputTokensPerSec, 3);

        // Run-level rollups.
        Assert.Equal(1000, m.TotalInputTokens);
        Assert.Equal(500, m.TotalOutputTokens);
        Assert.Equal(50d, m.AverageOutputTokensPerSec, 3);
    }

    [Fact]
    public void Recorder_MultipleTurns_NumbersAndRollsUp()
    {
        var rec = new RunMetricsRecorder();
        rec.Observe(new CliRunEvent.RunStarted(1, CliTypes.Claude, "claude-opus-4-8") { RunId = "r", ObservedAt = T0 });
        rec.Observe(new CliRunEvent.TurnStarted { RunId = "r", ObservedAt = T0.AddSeconds(1) });
        rec.Observe(new CliRunEvent.TurnCompleted("input=100 output=20 cache_read=5") { RunId = "r", ObservedAt = T0.AddSeconds(3) });
        rec.Observe(new CliRunEvent.TurnStarted { RunId = "r", ObservedAt = T0.AddSeconds(4) });
        rec.Observe(new CliRunEvent.TurnCompleted("input=200 output=40") { RunId = "r", ObservedAt = T0.AddSeconds(8) });
        rec.Observe(new CliRunEvent.RunEnded(RunOutcome.Completed, null, 0, 8) { RunId = "r", ObservedAt = T0.AddSeconds(8) });

        var m = rec.Build();
        Assert.Equal(2, m.TurnCount);
        Assert.Equal(1, m.Turns[0].TurnNumber);
        Assert.Equal(2, m.Turns[1].TurnNumber);
        Assert.Equal(300, m.TotalInputTokens);      // 100 + 200
        Assert.Equal(60, m.TotalOutputTokens);      // 20 + 40
        Assert.Equal(5, m.TotalCachedInputTokens);
    }

    [Fact]
    public void Recorder_NoOutputOrSession_LeavesThoseMetricsNull()
    {
        var rec = new RunMetricsRecorder();
        rec.Observe(new CliRunEvent.RunStarted(1, CliTypes.Gemini, null) { RunId = "r", ObservedAt = T0 });
        rec.Observe(new CliRunEvent.RunEnded(RunOutcome.Failed, "boom", 1, 0.5) { RunId = "r", ObservedAt = T0.AddSeconds(1) });

        var m = rec.Build();
        Assert.Null(m.TimeToFirstOutputMs);
        Assert.Null(m.TimeToSessionIdMs);
        Assert.Equal(500d, m.TotalDurationMs!.Value);
        Assert.Equal(0, m.TurnCount);
        Assert.Equal("Failed", m.Outcome);
        Assert.Equal(0d, m.AverageOutputTokensPerSec);
        Assert.Equal("", m.Model);
    }
}

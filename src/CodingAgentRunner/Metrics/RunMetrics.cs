namespace CodingAgentRunner.Metrics;

/// <summary>Performance metrics for one turn, derived from the event stream.</summary>
public sealed record TurnMetrics
{
    /// <summary>1-based turn index within the run.</summary>
    public int TurnNumber { get; init; }

    /// <summary>Wall-clock from <c>TurnStarted</c> (or run start) to <c>TurnCompleted</c>, in ms.</summary>
    public double WallClockMs { get; init; }

    /// <summary>Fresh input tokens this turn.</summary>
    public long InputTokens { get; init; }

    /// <summary>Output tokens this turn.</summary>
    public long OutputTokens { get; init; }

    /// <summary>Cached input tokens this turn.</summary>
    public long CachedInputTokens { get; init; }

    /// <summary>Reasoning output tokens this turn (Codex), 0 when not reported.</summary>
    public long ReasoningOutputTokens { get; init; }

    /// <summary>Output tokens per second (<see cref="OutputTokens"/> ÷ turn seconds); 0 for a zero-length turn.</summary>
    public double OutputTokensPerSec { get; init; }
}

/// <summary>
/// Performance metrics for a whole run, reconstructed from the <see cref="Events.CliRunEvent"/>
/// stream alone — no instrumentation. Time anchors come from each event's
/// <see cref="Events.CliRunEvent.ObservedAt"/>; token counts from the per-turn usage
/// summaries; total duration from <c>RunEnded</c>. Build one with a
/// <see cref="RunMetricsRecorder"/>.
/// </summary>
public sealed record RunMetrics
{
    /// <summary>The run id.</summary>
    public string RunId { get; init; } = "";

    /// <summary>The CLI that ran (one of <see cref="Model.CliTypes"/>).</summary>
    public string CliType { get; init; } = "";

    /// <summary>The model id, when reported.</summary>
    public string Model { get; init; } = "";

    /// <summary>Time-to-first-output: run start → first <c>OutputDelta</c>, in ms. Null if no output was seen.</summary>
    public double? TimeToFirstOutputMs { get; init; }

    /// <summary>Time-to-session-id: run start → first <c>SessionStarted</c>, in ms. Null if no session id surfaced.</summary>
    public double? TimeToSessionIdMs { get; init; }

    /// <summary>Total run wall-clock from <c>RunEnded.Duration</c>, in ms. Null if the run did not end.</summary>
    public double? TotalDurationMs { get; init; }

    /// <summary>Number of completed turns.</summary>
    public int TurnCount { get; init; }

    /// <summary>Per-turn metrics.</summary>
    public IReadOnlyList<TurnMetrics> Turns { get; init; } = [];

    /// <summary>Sum of fresh input tokens across turns.</summary>
    public long TotalInputTokens { get; init; }

    /// <summary>Sum of output tokens across turns.</summary>
    public long TotalOutputTokens { get; init; }

    /// <summary>Sum of cached input tokens across turns.</summary>
    public long TotalCachedInputTokens { get; init; }

    /// <summary>Sum of reasoning output tokens across turns.</summary>
    public long TotalReasoningOutputTokens { get; init; }

    /// <summary>Output tokens ÷ total turn seconds across the run; 0 when there were no timed turns.</summary>
    public double AverageOutputTokensPerSec { get; init; }

    /// <summary>The terminal outcome (<c>Completed</c> / <c>Stopped</c> / <c>Failed</c>), or empty if the run did not end.</summary>
    public string Outcome { get; init; } = "";
}

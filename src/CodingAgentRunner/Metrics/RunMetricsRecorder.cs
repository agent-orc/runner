using CodingAgentRunner.Events;

namespace CodingAgentRunner.Metrics;

/// <summary>
/// Folds a run's <see cref="CliRunEvent"/> stream into a <see cref="RunMetrics"/>.
/// Subscribe it to <c>ICliDriver.OnRunEvent</c> (or feed a recorded stream) and call
/// <see cref="Build"/> after the terminal <c>RunEnded</c>. Every metric is derived
/// from the events alone — <see cref="CliRunEvent.ObservedAt"/> for timing, the
/// per-turn usage summaries for tokens, <c>RunEnded.Duration</c> for the total — so a
/// consumer needs no extra instrumentation. Single-run, single-threaded use.
/// </summary>
public sealed class RunMetricsRecorder
{
    private DateTime? _start;
    private string _runId = "";
    private string _cliType = "";
    private string _model = "";
    private DateTime? _firstOutputAt;
    private DateTime? _sessionAt;
    private DateTime? _turnStart;
    private int _turnNumber;
    private double? _totalDurationMs;
    private string _outcome = "";
    private readonly List<TurnMetrics> _turns = [];

    /// <summary>Accumulate one event.</summary>
    public void Observe(CliRunEvent ev)
    {
        switch (ev)
        {
            case CliRunEvent.RunStarted rs:
                _start = ev.ObservedAt;
                _runId = ev.RunId;
                _cliType = rs.CliType;
                _model = rs.Model ?? "";
                break;
            case CliRunEvent.SessionStarted:
                _sessionAt ??= ev.ObservedAt;
                break;
            case CliRunEvent.OutputDelta:
                _firstOutputAt ??= ev.ObservedAt;
                break;
            case CliRunEvent.TurnStarted:
                _turnStart = ev.ObservedAt;
                break;
            case CliRunEvent.TurnCompleted tc:
                RecordTurn(tc, ev.ObservedAt);
                break;
            case CliRunEvent.RunEnded re:
                _totalDurationMs = re.Duration * 1000.0;
                _outcome = re.Outcome.ToString();
                if (string.IsNullOrEmpty(_runId)) _runId = ev.RunId;
                break;
        }
    }

    private void RecordTurn(CliRunEvent.TurnCompleted tc, DateTime completedAt)
    {
        var start = _turnStart ?? _start ?? completedAt;
        var wallMs = Math.Max(0, (completedAt - start).TotalMilliseconds);
        var u = UsageSummaryParser.Parse(tc.UsageSummary);
        var tps = wallMs > 0 ? u.Output / (wallMs / 1000.0) : 0;
        _turns.Add(new TurnMetrics
        {
            TurnNumber = ++_turnNumber,
            WallClockMs = wallMs,
            InputTokens = u.Input,
            OutputTokens = u.Output,
            CachedInputTokens = u.Cached,
            ReasoningOutputTokens = u.Reasoning,
            OutputTokensPerSec = tps,
        });
        _turnStart = null;
    }

    /// <summary>Finalize the accumulated events into a <see cref="RunMetrics"/>.</summary>
    public RunMetrics Build()
    {
        double? Since(DateTime? at) => _start is { } s && at is { } x ? (x - s).TotalMilliseconds : null;

        var totalOutput = _turns.Sum(t => t.OutputTokens);
        var totalTurnSeconds = _turns.Sum(t => t.WallClockMs) / 1000.0;

        return new RunMetrics
        {
            RunId = _runId,
            CliType = _cliType,
            Model = _model,
            TimeToFirstOutputMs = Since(_firstOutputAt),
            TimeToSessionIdMs = Since(_sessionAt),
            TotalDurationMs = _totalDurationMs,
            TurnCount = _turns.Count,
            Turns = _turns.ToList(),
            TotalInputTokens = _turns.Sum(t => t.InputTokens),
            TotalOutputTokens = totalOutput,
            TotalCachedInputTokens = _turns.Sum(t => t.CachedInputTokens),
            TotalReasoningOutputTokens = _turns.Sum(t => t.ReasoningOutputTokens),
            AverageOutputTokensPerSec = totalTurnSeconds > 0 ? totalOutput / totalTurnSeconds : 0,
            Outcome = _outcome,
        };
    }
}

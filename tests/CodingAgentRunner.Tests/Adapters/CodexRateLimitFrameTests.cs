using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using CodingAgentRunner.Model;
using CodingAgentRunner.Quota;
using Xunit;

namespace CodingAgentRunner.Tests.Adapters;

/// <summary>
/// The Codex core-protocol <c>token_count</c> frame (rollout logs / app-server;
/// not the exec stream as of codex 0.142) maps onto per-window
/// <see cref="CliRunEvent.RateLimitObserved"/> events with precise percent.
/// </summary>
public class CodexRateLimitFrameTests
{
    private const string TokenCountFrame =
        """{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":12.5,"window_minutes":300,"resets_at":1783031434},"secondary":{"used_percent":3.0,"window_minutes":10080,"resets_at":1783618234},"credits":null,"individual_limit":null,"plan_type":"pro","rate_limit_reached_type":null}}""";

    [Fact]
    public void Token_count_maps_to_one_rate_limit_event_per_window()
    {
        var events = CodexEventAdapter.Map(TokenCountFrame, "r1").ToList();

        Assert.Equal(2, events.Count);
        var rateLimits = events.Cast<CliRunEvent.RateLimitObserved>().ToList();

        var primary = rateLimits[0];
        Assert.Equal("5-hour", primary.Window);
        Assert.Equal(12.5, primary.UsedPercent);
        Assert.Equal(1783031434, primary.ResetsAt);
        Assert.Equal("allowed", primary.Status);

        var secondary = rateLimits[1];
        Assert.Equal("weekly", secondary.Window);
        Assert.Equal(3.0, secondary.UsedPercent);
    }

    [Fact]
    public void Token_count_without_rate_limits_maps_to_nothing()
    {
        Assert.Empty(CodexEventAdapter.Map("""{"type":"token_count","info":{},"rate_limits":null}""", "r1"));
        Assert.Empty(CodexEventAdapter.Map("""{"type":"token_count"}""", "r1"));
    }

    [Fact]
    public void Reached_limit_is_reflected_in_the_status()
    {
        var frame = TokenCountFrame.Replace("\"rate_limit_reached_type\":null", "\"rate_limit_reached_type\":\"primary\"");

        var evt = (CliRunEvent.RateLimitObserved)CodexEventAdapter.Map(frame, "r1").First();

        Assert.Equal("reached:primary", evt.Status);
    }

    [Fact]
    public void Adapter_events_and_session_log_probe_share_window_labels()
    {
        // Observe(event) and a probe refresh must merge into the SAME QuotaWindow —
        // both go through the label vocabulary of the Codex probe.
        var evt = (CliRunEvent.RateLimitObserved)CodexEventAdapter.Map(TokenCountFrame, "r1").First();

        var quota = new QuotaService(probes: []);
        quota.Observe(CliTypes.Codex, evt);

        Assert.True(CodexSessionLogProbe.TryParseRolloutLine(
            $$"""{"timestamp":"2026-07-02T17:31:07.403Z","type":"event_msg","payload":{{TokenCountFrame}}}""",
            out var probeSnapshot));

        var eventLabels = quota.GetCachedFor(CliTypes.Codex)!.Windows.Select(w => w.Label);
        Assert.Contains(eventLabels.Single(), probeSnapshot!.Windows.Select(w => w.Label));
    }
}

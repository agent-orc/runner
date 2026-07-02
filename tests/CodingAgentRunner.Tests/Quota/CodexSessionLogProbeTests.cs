using CodingAgentRunner.Model;
using CodingAgentRunner.Quota;
using Xunit;

namespace CodingAgentRunner.Tests.Quota;

public class CodexSessionLogProbeTests : IDisposable
{
    // Line shape captured from a live ~/.codex/sessions rollout (codex 0.142).
    private const string RolloutLine =
        """{"timestamp":"2026-07-02T17:31:07.403Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"limit_id":"codex","limit_name":null,"primary":{"used_percent":12.5,"window_minutes":300,"resets_at":1783031434},"secondary":{"used_percent":3.0,"window_minutes":10080,"resets_at":1783618234},"credits":null,"individual_limit":null,"plan_type":"pro","rate_limit_reached_type":null}}}""";

    private readonly string _codexHome = Path.Combine(Path.GetTempPath(), "car-codex-" + Guid.NewGuid().ToString("N"));

    public CodexSessionLogProbeTests() => Directory.CreateDirectory(_codexHome);

    public void Dispose()
    {
        try { Directory.Delete(_codexHome, recursive: true); } catch { }
    }

    private string WriteRollout(string relativePath, params string[] lines)
    {
        var path = Path.Combine(_codexHome, "sessions", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllLines(path, lines);
        return path;
    }

    // ── Line parsing ────────────────────────────────────────────────────

    [Fact]
    public void Parses_the_verified_rollout_line_shape()
    {
        Assert.True(CodexSessionLogProbe.TryParseRolloutLine(RolloutLine, out var snap));

        Assert.Equal(CliTypes.Codex, snap!.CliType);
        Assert.Equal("pro", snap.Plan);
        Assert.Equal("session-log", snap.Source);
        Assert.Contains("2026-07-02T17:31:07", snap.RawSample);

        var fiveHour = Assert.Single(snap.Windows, w => w.Label == "5-hour");
        Assert.Equal(12.5, fiveHour.UsedPct);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1783031434).UtcDateTime, fiveHour.ResetAt);

        var weekly = Assert.Single(snap.Windows, w => w.Label == "weekly");
        Assert.Equal(3.0, weekly.UsedPct);
        Assert.Equal(12.5, snap.MaxUsedPct);
    }

    [Theory]
    [InlineData("""{"type":"event_msg","payload":{"type":"token_count","info":{},"rate_limits":null}}""")]
    [InlineData("""{"type":"event_msg","payload":{"type":"agent_message"}}""")]
    [InlineData("""{"type":"response_item"}""")]
    [InlineData("not json")]
    public void Rejects_lines_without_usable_rate_limits(string line)
    {
        Assert.False(CodexSessionLogProbe.TryParseRolloutLine(line, out _));
    }

    [Fact]
    public void Legacy_resets_in_seconds_is_anchored_on_the_line_timestamp()
    {
        const string legacyLine =
            """{"timestamp":"2026-07-02T12:00:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":40.0,"window_minutes":300,"resets_in_seconds":3600},"secondary":null,"plan_type":"plus"}}}""";

        Assert.True(CodexSessionLogProbe.TryParseRolloutLine(legacyLine, out var snap));

        var window = Assert.Single(snap!.Windows);
        Assert.Equal(40.0, window.UsedPct);
        Assert.Equal(new DateTime(2026, 7, 2, 13, 0, 0, DateTimeKind.Utc), window.ResetAt);
    }

    [Theory]
    [InlineData(300.0, "5-hour")]
    [InlineData(10080.0, "weekly")]
    [InlineData(60.0, "60-minute")]
    [InlineData(null, "primary")]   // no window_minutes → the window keeps its own name; primary/secondary must not collapse
    public void Window_labels_match_the_claude_probe_vocabulary(double? minutes, string expected)
    {
        Assert.Equal(expected, CodexSessionLogProbe.WindowLabel(minutes, "primary"));
    }

    [Fact]
    public void Null_valued_window_fields_do_not_throw()
    {
        // Codex serializes absent optionals as explicit JSON null.
        const string line =
            """{"timestamp":"2026-07-02T12:00:00.000Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":12.5,"window_minutes":null,"resets_at":null},"secondary":null,"plan_type":null,"rate_limit_reached_type":null}}}""";

        Assert.True(CodexSessionLogProbe.TryParseRolloutLine(line, out var snap));

        var window = Assert.Single(snap!.Windows);
        Assert.Equal("primary", window.Label);
        Assert.Equal(12.5, window.UsedPct);
        Assert.Null(window.ResetAt);
    }

    [Fact]
    public async Task Probes_the_tail_of_a_file_larger_than_the_tail_window()
    {
        var filler = """{"type":"event_msg","payload":{"type":"agent_message","message":"PAD"}}"""
            .Replace("PAD", new string('x', 2048));
        var lines = Enumerable.Repeat(filler, 1500).Append(RolloutLine).ToArray();   // ~3 MB, entry at the very end
        WriteRollout(Path.Combine("2026", "07", "02", "rollout-big.jsonl"), lines);

        var snap = await new CodexSessionLogProbe(codexHome: _codexHome).ProbeAsync(CancellationToken.None);

        Assert.Null(snap.Error);
        Assert.Equal(12.5, snap.MaxUsedPct);
    }

    // ── ProbeAsync over the file system ─────────────────────────────────

    [Fact]
    public async Task Missing_sessions_dir_yields_error_with_the_fix()
    {
        var snap = await new CodexSessionLogProbe(codexHome: _codexHome).ProbeAsync(CancellationToken.None);

        Assert.Contains("run codex once", snap.Error);
    }

    [Fact]
    public async Task Reads_the_last_rate_limit_entry_of_the_newest_rollout()
    {
        var older = RolloutLine.Replace("12.5", "50.0");
        WriteRollout(Path.Combine("2026", "07", "01", "rollout-2026-07-01T10-00-00-old.jsonl"), older);
        var oldPath = Path.Combine(_codexHome, "sessions", "2026", "07", "01", "rollout-2026-07-01T10-00-00-old.jsonl");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddDays(-1));

        WriteRollout(Path.Combine("2026", "07", "02", "rollout-2026-07-02T19-31-05-new.jsonl"),
            """{"type":"event_msg","payload":{"type":"agent_message","message":"hi"}}""",
            RolloutLine.Replace("12.5", "7.5"),   // superseded by the later entry below
            RolloutLine);

        var snap = await new CodexSessionLogProbe(codexHome: _codexHome).ProbeAsync(CancellationToken.None);

        Assert.Null(snap.Error);
        Assert.Equal(12.5, snap.Windows.Single(w => w.Label == "5-hour").UsedPct);
    }

    [Fact]
    public async Task Rollouts_without_rate_limits_fall_through_to_older_files()
    {
        WriteRollout(Path.Combine("2026", "07", "01", "rollout-a.jsonl"), RolloutLine);
        var oldPath = Path.Combine(_codexHome, "sessions", "2026", "07", "01", "rollout-a.jsonl");
        File.SetLastWriteTimeUtc(oldPath, DateTime.UtcNow.AddHours(-2));

        WriteRollout(Path.Combine("2026", "07", "02", "rollout-b.jsonl"),
            """{"type":"event_msg","payload":{"type":"agent_message","message":"no limits here"}}""");

        var snap = await new CodexSessionLogProbe(codexHome: _codexHome).ProbeAsync(CancellationToken.None);

        Assert.Null(snap.Error);
        Assert.Equal(12.5, snap.MaxUsedPct);
    }

    [Fact]
    public async Task No_rate_limit_data_anywhere_yields_error()
    {
        WriteRollout(Path.Combine("2026", "07", "02", "rollout-empty.jsonl"),
            """{"type":"event_msg","payload":{"type":"agent_message","message":"nothing"}}""");

        var snap = await new CodexSessionLogProbe(codexHome: _codexHome).ProbeAsync(CancellationToken.None);

        Assert.NotNull(snap.Error);
    }
}

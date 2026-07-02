using System.Net;
using CodingAgentRunner.Events;
using CodingAgentRunner.Model;
using CodingAgentRunner.Quota;
using Xunit;

namespace CodingAgentRunner.Tests.Quota;

public class ClaudeOAuthUsageProbeTests : IDisposable
{
    // Field names and shape captured from a live api.anthropic.com/api/oauth/usage response.
    private const string UsageJson = """
    {
      "five_hour":  { "utilization": 14.0, "resets_at": "2026-07-02T21:10:00.549926+00:00" },
      "seven_day":  { "utilization": 5.0,  "resets_at": "2026-07-07T11:59:59.549952+00:00" },
      "limits": [
        { "kind": "session",       "group": "session", "percent": 14, "resets_at": "2026-07-02T21:10:00.549926+00:00", "scope": null, "is_active": true },
        { "kind": "weekly_all",    "group": "weekly",  "percent": 5,  "resets_at": "2026-07-07T11:59:59.549952+00:00", "scope": null, "is_active": false },
        { "kind": "weekly_scoped", "group": "weekly",  "percent": 8,  "resets_at": "2026-07-07T12:00:00.550355+00:00",
          "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null }, "is_active": false }
      ]
    }
    """;

    private const string CredentialsJson = """
    {
      "claudeAiOauth": {
        "accessToken": "sk-ant-oat-test",
        "refreshToken": "sk-ant-ort-test",
        "expiresAt": 253402300799000,
        "scopes": ["user:inference"],
        "subscriptionType": "max",
        "rateLimitTier": "default_claude_max_20x"
      }
    }
    """;

    private readonly string _configDir = Path.Combine(Path.GetTempPath(), "car-probe-" + Guid.NewGuid().ToString("N"));

    public ClaudeOAuthUsageProbeTests() => Directory.CreateDirectory(_configDir);

    public void Dispose()
    {
        try { Directory.Delete(_configDir, recursive: true); } catch { }
    }

    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    // ── ParseUsage ──────────────────────────────────────────────────────

    [Fact]
    public void ParseUsage_maps_windows_percent_and_resets()
    {
        var snap = ClaudeOAuthUsageProbe.ParseUsage(UsageJson, plan: "max");

        Assert.Null(snap.Error);
        Assert.Equal("max", snap.Plan);
        Assert.Equal(CliTypes.Claude, snap.CliType);

        var fiveHour = Assert.Single(snap.Windows, w => w.Label == "5-hour");
        Assert.Equal(14.0, fiveHour.UsedPct);
        Assert.Equal(new DateTime(2026, 7, 2, 21, 10, 0, 549, DateTimeKind.Utc).AddTicks(9260), fiveHour.ResetAt!.Value, TimeSpan.FromSeconds(1));

        var weekly = Assert.Single(snap.Windows, w => w.Label == "weekly");
        Assert.Equal(5.0, weekly.UsedPct);

        // Scoped per-model window is included; session/weekly_all duplicates are not.
        var scoped = Assert.Single(snap.Windows, w => w.Label.Contains("Fable"));
        Assert.Equal(8.0, scoped.UsedPct);
        Assert.Equal(3, snap.Windows.Count);

        Assert.Equal(14.0, snap.MaxUsedPct);
    }

    [Fact]
    public void ParseUsage_tolerates_missing_windows_and_limits()
    {
        var snap = ClaudeOAuthUsageProbe.ParseUsage("""{ "five_hour": { "utilization": 2.5 } }""", plan: null);

        var w = Assert.Single(snap.Windows);
        Assert.Equal(2.5, w.UsedPct);
        Assert.Null(w.ResetAt);
    }

    // ── ReadCredentials ─────────────────────────────────────────────────

    [Fact]
    public void ReadCredentials_extracts_token_expiry_and_plan()
    {
        var (token, expiresAt, plan) = ClaudeOAuthUsageProbe.ReadCredentials(CredentialsJson);

        Assert.Equal("sk-ant-oat-test", token);
        Assert.Equal(253402300799000, expiresAt);
        Assert.Equal("max (default_claude_max_20x)", plan);
    }

    [Fact]
    public void ReadCredentials_handles_missing_oauth_section()
    {
        var (token, expiresAt, plan) = ClaudeOAuthUsageProbe.ReadCredentials("{}");

        Assert.Null(token);
        Assert.Null(expiresAt);
        Assert.Null(plan);
    }

    // ── ProbeAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task Missing_credentials_file_yields_error_snapshot_with_the_fix()
    {
        var probe = new ClaudeOAuthUsageProbe(configDir: _configDir);

        var snap = await probe.ProbeAsync(CancellationToken.None);

        Assert.NotNull(snap.Error);
        Assert.Contains("claude", snap.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(snap.Windows);
    }

    [Fact]
    public async Task Expired_token_yields_error_without_calling_the_endpoint()
    {
        var expired = CredentialsJson.Replace("253402300799000", "1000");
        await File.WriteAllTextAsync(Path.Combine(_configDir, ".credentials.json"), expired);
        var handler = new StubHandler(HttpStatusCode.OK, UsageJson);

        var snap = await new ClaudeOAuthUsageProbe(new HttpClient(handler), configDir: _configDir)
            .ProbeAsync(CancellationToken.None);

        Assert.Contains("expired", snap.Error, StringComparison.OrdinalIgnoreCase);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task Happy_path_probes_the_endpoint_with_oauth_headers()
    {
        await File.WriteAllTextAsync(Path.Combine(_configDir, ".credentials.json"), CredentialsJson);
        var handler = new StubHandler(HttpStatusCode.OK, UsageJson);

        var snap = await new ClaudeOAuthUsageProbe(new HttpClient(handler), configDir: _configDir)
            .ProbeAsync(CancellationToken.None);

        Assert.Null(snap.Error);
        Assert.Equal(3, snap.Windows.Count);
        Assert.Equal("max (default_claude_max_20x)", snap.Plan);
        Assert.Equal("oauth-usage-endpoint", snap.Source);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-ant-oat-test", handler.LastRequest.Headers.GetValues("Authorization").Single());
        Assert.Equal("oauth-2025-04-20", handler.LastRequest.Headers.GetValues("anthropic-beta").Single());
    }

    [Fact]
    public async Task Http_error_yields_error_snapshot_not_exception()
    {
        await File.WriteAllTextAsync(Path.Combine(_configDir, ".credentials.json"), CredentialsJson);
        var handler = new StubHandler(HttpStatusCode.Unauthorized, """{"error":"unauthorized"}""");

        var snap = await new ClaudeOAuthUsageProbe(new HttpClient(handler), configDir: _configDir)
            .ProbeAsync(CancellationToken.None);

        Assert.Contains("401", snap.Error);
    }
}

public class RateLimitPercentHarvestTests
{
    [Fact]
    public void Observe_with_percent_sets_usage_and_may_lower_it()
    {
        var quota = new QuotaService(probes: []);

        // Event with a precise percent establishes the figure...
        quota.Observe(CliTypes.Codex, new CliRunEvent.RateLimitObserved(
            "primary", "ok", 0, null, false, UsedPercent: 42.5) { RunId = "r1" });
        Assert.Equal(42.5, quota.GetCachedFor(CliTypes.Codex)!.Windows.Single().UsedPct);

        // ...and a later precise percent is authoritative even when lower (window reset).
        quota.Observe(CliTypes.Codex, new CliRunEvent.RateLimitObserved(
            "primary", "ok", 0, null, false, UsedPercent: 3.0) { RunId = "r1" });
        Assert.Equal(3.0, quota.GetCachedFor(CliTypes.Codex)!.Windows.Single().UsedPct);
    }

    [Fact]
    public void Observe_without_percent_keeps_prior_figure()
    {
        var quota = new QuotaService(probes: []);

        quota.Observe(CliTypes.Claude, new CliRunEvent.RateLimitObserved(
            "five_hour", "allowed", 0, null, false, UsedPercent: 60) { RunId = "r1" });
        quota.Observe(CliTypes.Claude, new CliRunEvent.RateLimitObserved(
            "five_hour", "allowed", 1783026600, null, false) { RunId = "r1" });

        var window = quota.GetCachedFor(CliTypes.Claude)!.Windows.Single();
        Assert.Equal(60, window.UsedPct);                       // not lowered by a percent-less event
        Assert.NotNull(window.ResetAt);                          // but the reset time was refreshed
    }

    [Fact]
    public void Observe_overage_without_percent_still_pins_100()
    {
        var quota = new QuotaService(probes: []);

        quota.Observe(CliTypes.Claude, new CliRunEvent.RateLimitObserved(
            "five_hour", "allowed_warning", 0, "allowed", IsUsingOverage: true) { RunId = "r1" });

        Assert.Equal(100, quota.GetCachedFor(CliTypes.Claude)!.Windows.Single().UsedPct);
    }
}

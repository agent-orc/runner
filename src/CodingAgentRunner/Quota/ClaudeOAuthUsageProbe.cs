using System.Text.Json;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Quota;

/// <summary>
/// Built-in quota probe for Claude Code. Reads the OAuth access token the CLI
/// stores after sign-in (<c>~/.claude/.credentials.json</c>, or
/// <c>$CLAUDE_CONFIG_DIR/.credentials.json</c>) and calls the same usage endpoint
/// the CLI's <c>/usage</c> screen uses
/// (<c>GET https://api.anthropic.com/api/oauth/usage</c>). The response carries
/// real server-side utilization percent and reset time per window (5-hour
/// session, weekly, plus model-scoped weekly windows when present).
///
/// <para><b>Limits.</b> The endpoint is the CLI's own, not a documented public
/// API — a Claude Code update can change it; the probe then reports an
/// <see cref="QuotaSnapshot.Error"/> rather than throwing. The probe never
/// refreshes the token (that is the CLI's job): an expired token yields an error
/// snapshot that names the fix (run <c>claude</c> once). On macOS the CLI stores
/// credentials in the Keychain, not the file — the probe reports the missing
/// file. API-key sign-ins (<c>ANTHROPIC_API_KEY</c>) have no subscription
/// windows; the probe only serves OAuth (subscription) sign-ins.</para>
/// </summary>
public sealed class ClaudeOAuthUsageProbe : IQuotaProbe
{
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private const string OAuthBetaHeader = "oauth-2025-04-20";

    // The endpoint rate-limits requests without a claude-code User-Agent into a
    // persistent-429 bucket (community-documented); identify like the CLI, with
    // this library appended as a product token.
    private const string UserAgent = "claude-code/2.1.198 CodingAgentRunner";

    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _http;
    private readonly IUserHomeProvider _home;
    private readonly string? _configDirOverride;

    /// <summary>
    /// Create the probe. All parameters are optional: <paramref name="http"/> for a
    /// custom pipeline (tests inject a fake handler), <paramref name="home"/> for a
    /// non-default user home, <paramref name="configDir"/> to bypass the
    /// <c>CLAUDE_CONFIG_DIR</c>/<c>~/.claude</c> resolution entirely.
    /// </summary>
    public ClaudeOAuthUsageProbe(HttpClient? http = null, IUserHomeProvider? home = null, string? configDir = null)
    {
        _http = http ?? SharedHttp;
        _home = home ?? new DefaultUserHomeProvider();
        _configDirOverride = configDir;
    }

    /// <inheritdoc />
    public string CliType => CliTypes.Claude;

    /// <inheritdoc />
    public async Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        var credentialsPath = Path.Combine(ResolveConfigDir(), ".credentials.json");
        if (!File.Exists(credentialsPath))
            return Error($"credentials not found at '{credentialsPath}' — sign in by running `claude` once (on macOS the CLI uses the Keychain, which this probe cannot read)");

        string credentialsJson;
        try { credentialsJson = await File.ReadAllTextAsync(credentialsPath, ct).ConfigureAwait(false); }
        catch (Exception ex) { return Error($"cannot read '{credentialsPath}': {ex.Message}"); }

        var (token, expiresAtMs, plan) = ReadCredentials(credentialsJson);
        if (string.IsNullOrWhiteSpace(token))
            return Error($"no OAuth access token in '{credentialsPath}' (API-key sign-ins have no subscription usage)");
        if (expiresAtMs is { } exp && DateTimeOffset.FromUnixTimeMilliseconds(exp) <= DateTimeOffset.UtcNow)
            return Error("OAuth access token is expired — run `claude` once so the CLI refreshes it");

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, ct).ConfigureAwait(false); }
        catch (Exception ex) { return Error($"usage endpoint unreachable: {ex.Message}"); }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Error($"usage endpoint returned {(int)response.StatusCode} {response.StatusCode}");
            try { return ParseUsage(body, plan); }
            catch (Exception ex) { return Error($"usage response did not parse: {ex.Message}"); }
        }
    }

    private string ResolveConfigDir()
        => _configDirOverride
           ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR")
           ?? Path.Combine(_home.GetUserHome(), ".claude");

    private QuotaSnapshot Error(string message) => new() { CliType = CliTypes.Claude, Error = message };

    /// <summary>Extract (accessToken, expiresAt ms, plan) from <c>.credentials.json</c>. Tolerant of missing fields.</summary>
    internal static (string? Token, long? ExpiresAtMs, string? Plan) ReadCredentials(string credentialsJson)
    {
        using var doc = JsonDocument.Parse(credentialsJson);
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
            return (null, null, null);

        var token = oauth.TryGetProperty("accessToken", out var at) && at.ValueKind == JsonValueKind.String ? at.GetString() : null;
        long? expires = oauth.TryGetProperty("expiresAt", out var ea) && ea.TryGetInt64(out var e) ? e : null;

        string? plan = oauth.TryGetProperty("subscriptionType", out var st) && st.ValueKind == JsonValueKind.String ? st.GetString() : null;
        if (oauth.TryGetProperty("rateLimitTier", out var rt) && rt.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(rt.GetString()))
            plan = plan is null ? rt.GetString() : $"{plan} ({rt.GetString()})";
        return (token, expires, plan);
    }

    /// <summary>
    /// Map the usage response onto <see cref="QuotaWindow"/>s: the top-level
    /// <c>five_hour</c>/<c>seven_day</c> objects (utilization percent + reset), plus
    /// any scoped entries from <c>limits[]</c> (e.g. a per-model weekly window) that
    /// the two top-level objects do not already cover.
    /// </summary>
    internal static QuotaSnapshot ParseUsage(string usageJson, string? plan)
    {
        using var doc = JsonDocument.Parse(usageJson);
        var root = doc.RootElement;
        var windows = new List<QuotaWindow>();

        AddWindow(windows, root, "five_hour", "5-hour");
        AddWindow(windows, root, "seven_day", "weekly");

        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object) continue;
                var kind = limit.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
                // "session" duplicates five_hour and "weekly_all" duplicates seven_day.
                if (kind is null or "session" or "weekly_all") continue;

                var label = kind;
                if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object
                    && model.TryGetProperty("display_name", out var dn) && dn.ValueKind == JsonValueKind.String)
                    label = $"{kind} ({dn.GetString()})";

                windows.Add(new QuotaWindow
                {
                    Label = label!,
                    UsedPct = limit.TryGetProperty("percent", out var pct) && pct.TryGetDouble(out var p) ? p : null,
                    Unit = "%",
                    ResetAt = ReadResetAt(limit),
                });
            }
        }

        return new QuotaSnapshot
        {
            CliType = CliTypes.Claude,
            Plan = plan,
            Windows = windows,
            Source = "oauth-usage-endpoint",
            RawSample = Truncate(usageJson, 300),
        };
    }

    private static void AddWindow(List<QuotaWindow> windows, JsonElement root, string property, string label)
    {
        if (!root.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object) return;
        windows.Add(new QuotaWindow
        {
            Label = label,
            UsedPct = el.TryGetProperty("utilization", out var u) && u.TryGetDouble(out var pct) ? pct : null,
            Unit = "%",
            ResetAt = ReadResetAt(el),
        });
    }

    private static DateTime? ReadResetAt(JsonElement el)
        => el.TryGetProperty("resets_at", out var ra) && ra.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(ra.GetString(), out var dto)
            ? dto.UtcDateTime
            : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}

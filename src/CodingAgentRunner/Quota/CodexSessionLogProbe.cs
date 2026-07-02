using System.Text.Json;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Quota;

/// <summary>
/// Built-in quota probe for the Codex CLI. Codex writes every run to a rollout
/// log (<c>~/.codex/sessions/YYYY/MM/DD/rollout-*.jsonl</c>, or under
/// <c>$CODEX_HOME</c>), and each run records <c>token_count</c> events whose
/// <c>rate_limits</c> payload carries real server-side usage: a primary window
/// (300&#160;min = 5-hour) and a secondary window (10&#160;080&#160;min = weekly), each with
/// <c>used_percent</c> and <c>resets_at</c>, plus <c>plan_type</c>. The probe reads
/// the newest rollout files and returns the most recent figures — no process is
/// spawned and no quota is consumed.
///
/// <para><b>Freshness.</b> The data is as old as the last Codex run on this
/// machine; the snapshot's <see cref="QuotaSnapshot.RawSample"/> names the source
/// event's timestamp. For live updates during runs, also wire
/// <see cref="QuotaService.Observe"/> — the Codex adapter surfaces the same
/// payload as <see cref="Events.CliRunEvent.RateLimitObserved"/> with a precise
/// percent. When no rollout contains rate-limit data (never run, or a fresh
/// sign-in), the probe reports an <see cref="QuotaSnapshot.Error"/> naming the
/// fix.</para>
/// </summary>
public sealed class CodexSessionLogProbe : IQuotaProbe
{
    // Newest-first cap on how many rollout files are scanned per probe: a machine
    // with heavy Codex use has thousands; the freshest rate_limits entry is
    // virtually always in the most recent few.
    private const int MaxFilesScanned = 5;
    private const long MaxFileBytes = 32 * 1024 * 1024;

    private readonly IUserHomeProvider _home;
    private readonly string? _codexHomeOverride;

    /// <summary>Create the probe. <paramref name="codexHome"/> bypasses the <c>CODEX_HOME</c>/<c>~/.codex</c> resolution (tests, non-standard installs).</summary>
    public CodexSessionLogProbe(IUserHomeProvider? home = null, string? codexHome = null)
    {
        _home = home ?? new DefaultUserHomeProvider();
        _codexHomeOverride = codexHome;
    }

    /// <inheritdoc />
    public string CliType => CliTypes.Codex;

    /// <inheritdoc />
    public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct)
    {
        var sessionsDir = Path.Combine(ResolveCodexHome(), "sessions");
        if (!Directory.Exists(sessionsDir))
            return Task.FromResult(Error($"no codex session logs at '{sessionsDir}' — run codex once, then re-probe"));

        List<FileInfo> newestFirst;
        try
        {
            newestFirst = new DirectoryInfo(sessionsDir)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFilesScanned)
                .ToList();
        }
        catch (Exception ex) { return Task.FromResult(Error($"cannot enumerate '{sessionsDir}': {ex.Message}")); }

        foreach (var file in newestFirst)
        {
            ct.ThrowIfCancellationRequested();
            if (file.Length > MaxFileBytes) continue;

            string[] lines;
            try { lines = File.ReadAllLines(file.FullName); }
            catch { continue; }   // a live run may hold the newest file; fall back to the next

            // Scan backwards: the LAST token_count in the newest file is the freshest state.
            for (var i = lines.Length - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("rate_limits", StringComparison.Ordinal)) continue;
                if (TryParseRolloutLine(lines[i], out var snapshot))
                    return Task.FromResult(snapshot!);
            }
        }

        return Task.FromResult(Error("no rate-limit data in the recent codex session logs — run codex once, then re-probe"));
    }

    private string ResolveCodexHome()
        => _codexHomeOverride
           ?? Environment.GetEnvironmentVariable("CODEX_HOME")
           ?? Path.Combine(_home.GetUserHome(), ".codex");

    private static QuotaSnapshot Error(string message) => new() { CliType = CliTypes.Codex, Error = message };

    /// <summary>
    /// Parse one rollout JSONL line; true when it is a <c>token_count</c> event with
    /// a usable <c>rate_limits</c> object. Line shape (verified against codex 0.142):
    /// <c>{"timestamp":"…","type":"event_msg","payload":{"type":"token_count","info":…,"rate_limits":{"primary":{"used_percent":…,"window_minutes":300,"resets_at":…},"secondary":{…},"plan_type":"pro",…}}}</c>.
    /// </summary>
    internal static bool TryParseRolloutLine(string jsonLine, out QuotaSnapshot? snapshot)
    {
        snapshot = null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(jsonLine); }
        catch { return false; }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return false;
            if (!payload.TryGetProperty("type", out var pt) || pt.GetString() != "token_count") return false;
            if (!payload.TryGetProperty("rate_limits", out var rateLimits) || rateLimits.ValueKind != JsonValueKind.Object) return false;

            var observedAt = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() : null;
            snapshot = FromRateLimits(rateLimits, observedAt);
            return snapshot.Windows.Count > 0;
        }
    }

    /// <summary>Map a Codex <c>rate_limits</c> object (from a rollout log or a live frame) onto a snapshot.</summary>
    internal static QuotaSnapshot FromRateLimits(JsonElement rateLimits, string? observedAt)
    {
        var windows = new List<QuotaWindow>();
        AddWindow(windows, rateLimits, "primary");
        AddWindow(windows, rateLimits, "secondary");

        var plan = rateLimits.TryGetProperty("plan_type", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
        return new QuotaSnapshot
        {
            CliType = CliTypes.Codex,
            Plan = plan,
            Windows = windows,
            Source = "session-log",
            RawSample = observedAt is null ? null : $"observed at {observedAt} (age of the last codex run)",
        };
    }

    private static void AddWindow(List<QuotaWindow> windows, JsonElement rateLimits, string property)
    {
        if (!rateLimits.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object) return;

        double? minutes = el.TryGetProperty("window_minutes", out var wm) && wm.TryGetDouble(out var m) ? m : null;
        windows.Add(new QuotaWindow
        {
            Label = WindowLabel(minutes),
            UsedPct = el.TryGetProperty("used_percent", out var up) && up.TryGetDouble(out var pct) ? pct : null,
            Unit = "%",
            ResetAt = el.TryGetProperty("resets_at", out var ra) && ra.TryGetInt64(out var reset) && reset > 0
                ? DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime
                : null,
        });
    }

    /// <summary>Codex windows are sized in minutes; name the two known ones like the Claude windows for a uniform report.</summary>
    internal static string WindowLabel(double? windowMinutes) => windowMinutes switch
    {
        300 => "5-hour",
        10080 => "weekly",
        null => "window",
        _ => $"{windowMinutes:0}-minute",
    };
}

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

    // Only the TAIL of each rollout is read (the last token_count is the freshest
    // state), so a multi-gigabyte live session costs the same as a small one.
    private const int TailBytes = 1024 * 1024;

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
    /// <remarks>The scan runs on the thread pool so a fire-and-forget background refresh never blocks its caller on file IO.</remarks>
    public Task<QuotaSnapshot> ProbeAsync(CancellationToken ct) => Task.Run(() => ProbeCore(ct), ct);

    private QuotaSnapshot ProbeCore(CancellationToken ct)
    {
        var sessionsDir = Path.Combine(ResolveCodexHome(), "sessions");
        if (!Directory.Exists(sessionsDir))
            return Error($"no codex session logs at '{sessionsDir}' — run codex once, then re-probe");

        List<FileInfo> newestFirst;
        try
        {
            newestFirst = new DirectoryInfo(sessionsDir)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(MaxFilesScanned)
                .ToList();
        }
        catch (Exception ex) { return Error($"cannot enumerate '{sessionsDir}': {ex.Message}"); }

        foreach (var file in newestFirst)
        {
            ct.ThrowIfCancellationRequested();

            List<string> lines;
            try { lines = ReadTailLines(file.FullName, TailBytes); }
            catch { continue; }   // a live run may hold the newest file; fall back to the next

            // Scan backwards: the LAST token_count in the newest file is the freshest state.
            for (var i = lines.Count - 1; i >= 0; i--)
            {
                if (!lines[i].Contains("rate_limits", StringComparison.Ordinal)) continue;
                if (TryParseRolloutLine(lines[i], out var snapshot))
                    return snapshot!;
            }
        }

        return Error("no rate-limit data in the recent codex session logs — run codex once, then re-probe");
    }

    /// <summary>
    /// The complete lines within the last <paramref name="maxBytes"/> of the file
    /// (shared read, so a live codex run holding the file does not fail the probe).
    /// When the file is longer, the first — almost certainly partial — line of the
    /// tail window is dropped.
    /// </summary>
    private static List<string> ReadTailLines(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var skippedPartialStart = false;
        if (stream.Length > maxBytes)
        {
            stream.Seek(-maxBytes, SeekOrigin.End);
            skippedPartialStart = true;
        }

        using var reader = new StreamReader(stream);
        var lines = new List<string>();
        string? line;
        while ((line = reader.ReadLine()) is not null) lines.Add(line);
        if (skippedPartialStart && lines.Count > 0) lines.RemoveAt(0);
        return lines;
    }

    private string ResolveCodexHome()
    {
        if (!string.IsNullOrWhiteSpace(_codexHomeOverride)) return _codexHomeOverride!;
        var env = Environment.GetEnvironmentVariable("CODEX_HOME");
        return string.IsNullOrWhiteSpace(env) ? Path.Combine(_home.GetUserHome(), ".codex") : env!;
    }

    private static QuotaSnapshot Error(string message) => new() { CliType = CliTypes.Codex, Error = message };

    /// <summary>
    /// Parse one rollout JSONL line; true when it is a <c>token_count</c> event with
    /// a usable <c>rate_limits</c> object. Line shape (verified against codex 0.142):
    /// <c>{"timestamp":"…","type":"event_msg","payload":{"type":"token_count","info":…,"rate_limits":{"primary":{"used_percent":…,"window_minutes":300,"resets_at":…},"secondary":{…},"plan_type":"pro",…}}}</c>.
    /// </summary>
    internal static bool TryParseRolloutLine(string jsonLine, out QuotaSnapshot? snapshot)
    {
        snapshot = null;
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) return false;
            if (!payload.TryGetProperty("type", out var pt) || pt.ValueKind != JsonValueKind.String || pt.GetString() != "token_count") return false;
            if (!payload.TryGetProperty("rate_limits", out var rateLimits) || rateLimits.ValueKind != JsonValueKind.Object) return false;

            var observedAt = root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String ? ts.GetString() : null;
            snapshot = FromRateLimits(rateLimits, observedAt);
            return snapshot.Windows.Count > 0;
        }
        catch
        {
            // One odd line must not abort the probe — the caller falls back to the
            // next line / older rollout.
            snapshot = null;
            return false;
        }
    }

    /// <summary>Map a Codex <c>rate_limits</c> object (from a rollout log or a live frame) onto a snapshot.</summary>
    internal static QuotaSnapshot FromRateLimits(JsonElement rateLimits, string? observedAt)
    {
        // Rollouts written by pre-0.14x codex carry a RELATIVE `resets_in_seconds`
        // instead of the absolute `resets_at`; anchor it on the line's timestamp.
        DateTime? observed = DateTimeOffset.TryParse(observedAt, out var dto) ? dto.UtcDateTime : null;

        var windows = new List<QuotaWindow>();
        AddWindow(windows, rateLimits, "primary", observed);
        AddWindow(windows, rateLimits, "secondary", observed);
        if (windows.Count == 0) return new QuotaSnapshot { CliType = CliTypes.Codex };

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

    private static void AddWindow(List<QuotaWindow> windows, JsonElement rateLimits, string property, DateTime? observed)
    {
        if (!rateLimits.TryGetProperty(property, out var el) || el.ValueKind != JsonValueKind.Object) return;

        // Codex serializes absent optionals as explicit JSON null — gate every
        // numeric read on the value kind, or TryGet* throws.
        double? minutes = el.TryGetProperty("window_minutes", out var wm)
            && wm.ValueKind == JsonValueKind.Number && wm.TryGetDouble(out var m) ? m : null;

        DateTime? resetAt = null;
        if (el.TryGetProperty("resets_at", out var ra)
            && ra.ValueKind == JsonValueKind.Number && ra.TryGetInt64(out var reset) && reset > 0)
            resetAt = DateTimeOffset.FromUnixTimeSeconds(reset).UtcDateTime;
        else if (observed is { } at
                 && el.TryGetProperty("resets_in_seconds", out var ris)
                 && ris.ValueKind == JsonValueKind.Number && ris.TryGetInt64(out var seconds) && seconds > 0)
            resetAt = at.AddSeconds(seconds);

        windows.Add(new QuotaWindow
        {
            Label = WindowLabel(minutes, property),
            UsedPct = el.TryGetProperty("used_percent", out var up)
                && up.ValueKind == JsonValueKind.Number && up.TryGetDouble(out var pct) ? pct : null,
            Unit = "%",
            ResetAt = resetAt,
        });
    }

    /// <summary>
    /// Codex windows are sized in minutes; name the two known ones like the Claude
    /// windows for a uniform report. A window without <c>window_minutes</c> keeps
    /// its own <paramref name="fallback"/> (the JSON property name), so primary and
    /// secondary never collapse onto one label — <see cref="QuotaService.Observe"/>
    /// merges by label, and a shared label would let one window's percent
    /// overwrite the other's.
    /// </summary>
    internal static string WindowLabel(double? windowMinutes, string fallback) => windowMinutes switch
    {
        300 => "5-hour",
        10080 => "weekly",
        null => fallback,
        _ => $"{windowMinutes:0}-minute",
    };
}

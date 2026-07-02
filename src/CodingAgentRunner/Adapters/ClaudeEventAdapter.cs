using System.Text.Json;
using CodingAgentRunner.Events;

namespace CodingAgentRunner.Adapters;

/// <summary>
/// Maps Claude Code's <c>--output-format stream-json --verbose</c> NDJSON frames
/// onto the typed <see cref="CliRunEvent"/> contract. Stateless with respect to a
/// single frame; the caller holds whatever state it needs across frames (e.g. the
/// captured session id).
///
/// <para>
/// Frame-to-event mapping. Each line of stdout from claude is one JSON object
/// whose <c>type</c> we switch on:
/// </para>
/// <list type="bullet">
///   <item><c>system</c> with <c>subtype=init</c> &#8594; <see cref="CliRunEvent.SessionStarted"/> (carries the id).</item>
///   <item><c>system</c> with any other subtype &#8594; <see cref="CliRunEvent.SessionInitializing"/>.</item>
///   <item><c>rate_limit_event</c> &#8594; <see cref="CliRunEvent.RateLimitObserved"/>.</item>
///   <item><c>assistant</c> with <c>tool_use</c> part &#8594; <see cref="CliRunEvent.ToolStarted"/>.</item>
///   <item><c>assistant</c> with <c>text</c> part &#8594; <see cref="CliRunEvent.OutputDelta"/>.</item>
///   <item><c>user</c> with <c>tool_result</c> part &#8594; <see cref="CliRunEvent.ToolCompleted"/> (<c>is_error</c> flag).</item>
///   <item><c>result</c> with <c>is_error=true</c> &#8594; <see cref="CliRunEvent.TurnFailed"/>.</item>
///   <item><c>result</c> with <c>is_error=false</c> &#8594; <see cref="CliRunEvent.TurnCompleted"/> — the CLI's real completion signal.</item>
///   <item>everything else &#8594; <see cref="CliRunEvent.Unknown"/> with the raw <c>type</c> as sample.</item>
/// </list>
/// </summary>
public static class ClaudeEventAdapter
{
    /// <summary>
    /// Map one stream-json line to zero or more <see cref="CliRunEvent"/> instances.
    /// A single line can produce multiple events (an assistant frame with a text
    /// part AND a tool_use part yields <see cref="CliRunEvent.OutputDelta"/>
    /// followed by <see cref="CliRunEvent.ToolStarted"/>).
    ///
    /// Lines that are not JSON, not stdout, or empty produce no events. Lines whose
    /// top-level <c>type</c> we cannot classify produce a single
    /// <see cref="CliRunEvent.Unknown"/> with a 200-char prefix of the original
    /// line as <see cref="CliRunEvent.Unknown.Sample"/>.
    /// </summary>
    public static IEnumerable<CliRunEvent> Map(string jsonLine, string runId)
    {
        if (string.IsNullOrWhiteSpace(jsonLine) || jsonLine[0] != '{') yield break;

        JsonDocument? doc = null;
        try { doc = JsonDocument.Parse(jsonLine); }
        catch { yield break; }

        using var _ = doc;
        if (doc.RootElement.ValueKind != JsonValueKind.Object) yield break;

        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        switch (type)
        {
            case "system":
            {
                var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : null;
                var sessionId = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
                if (string.Equals(subtype, "init", StringComparison.Ordinal))
                {
                    yield return new CliRunEvent.SessionStarted(sessionId) { RunId = runId };
                }
                else
                {
                    yield return new CliRunEvent.SessionInitializing { RunId = runId };
                }
                yield break;
            }
            case "rate_limit_event":
            {
                if (root.TryGetProperty("rate_limit_info", out var info) && info.ValueKind == JsonValueKind.Object)
                {
                    // Normalize the window label onto the built-in Claude probe's
                    // vocabulary (five_hour → 5-hour, seven_day → weekly):
                    // QuotaService.Observe merges by label, so a live event must
                    // land in the SAME QuotaWindow the probe wrote.
                    var window = Quota.ClaudeOAuthUsageProbe.WindowLabel(
                        info.TryGetProperty("rateLimitType", out var rlt) ? rlt.GetString() : null);
                    var status = info.TryGetProperty("status", out var s) ? s.GetString() : null;
                    var resetsAt = info.TryGetProperty("resetsAt", out var ra)
                        && ra.ValueKind == JsonValueKind.Number && ra.TryGetInt64(out var v) ? v : 0L;
                    var overage = info.TryGetProperty("overageStatus", out var os) ? os.GetString() : null;
                    var using_ = info.TryGetProperty("isUsingOverage", out var iuo) && iuo.ValueKind == JsonValueKind.True;
                    yield return new CliRunEvent.RateLimitObserved(window, status, resetsAt, overage, using_) { RunId = runId };
                }
                yield break;
            }
            case "assistant":
            {
                if (!root.TryGetProperty("message", out var msg)) yield break;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;

                // We do not emit a separate TurnStarted: claude has no distinct
                // "turn began" frame — OutputDelta on the first assistant text plays
                // that role.
                foreach (var part in content.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                    if (partType == "text")
                    {
                        var text = part.TryGetProperty("text", out var txt) ? txt.GetString() ?? "" : "";
                        if (!string.IsNullOrEmpty(text))
                            yield return new CliRunEvent.OutputDelta(text) { RunId = runId };
                    }
                    else if (partType == "tool_use")
                    {
                        var name = part.TryGetProperty("name", out var n) ? n.GetString() ?? "Tool" : "Tool";
                        var argument = ExtractToolArgument(part);
                        yield return new CliRunEvent.ToolStarted(name, argument) { RunId = runId };

                        // TodoWrite is Claude's native task-plan frame. Surface it as
                        // a typed PlanUpdated in addition to the tool call so a
                        // consumer can persist a plan snapshot.
                        if (string.Equals(name, "TodoWrite", StringComparison.Ordinal)
                            && TryExtractTodoPlan(part, out var items))
                        {
                            yield return new CliRunEvent.PlanUpdated("claude/TodoWrite", items) { RunId = runId };
                        }
                    }
                    else if (partType == "thinking")
                    {
                        // Extended-thinking — dropped from the typed stream. The raw
                        // bytes still reach the on-disk output log.
                    }
                    // unknown part types: ignored here.
                }
                yield break;
            }
            case "user":
            {
                if (!root.TryGetProperty("message", out var msg)) yield break;
                if (!msg.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) yield break;
                foreach (var part in content.EnumerateArray())
                {
                    var partType = part.TryGetProperty("type", out var pt) ? pt.GetString() : null;
                    if (partType != "tool_result") continue;
                    var isError = part.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                    var name = "tool"; // tool_use_id is opaque; we report a generic name
                    var firstLine = ExtractFirstLine(part);
                    yield return new CliRunEvent.ToolCompleted(name, isError, firstLine) { RunId = runId };
                }
                yield break;
            }
            case "result":
            {
                var isError = root.TryGetProperty("is_error", out var ie) && ie.ValueKind == JsonValueKind.True;
                if (isError)
                {
                    var subtype = root.TryGetProperty("subtype", out var st) ? st.GetString() : "error";
                    yield return new CliRunEvent.TurnFailed(subtype ?? "error") { RunId = runId };
                }
                else
                {
                    var usage = root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object
                        ? FormatUsage(u)
                        : null;
                    yield return new CliRunEvent.TurnCompleted(usage) { RunId = runId };
                }
                yield break;
            }
            default:
            {
                yield return new CliRunEvent.Unknown(Truncate(jsonLine, 200)) { RunId = runId };
                yield break;
            }
        }
    }

    /// <summary>
    /// Pull the plan items out of a <c>TodoWrite</c> tool_use part. The input shape
    /// is <c>{"todos":[{"content","status","activeForm"}]}</c>; we use the
    /// imperative <c>content</c> as the stable title and normalize the status.
    /// Returns false when the frame carries no usable todos so the caller emits no
    /// PlanUpdated.
    /// </summary>
    private static bool TryExtractTodoPlan(JsonElement toolPart, out IReadOnlyList<PlanFrameItem> items)
    {
        items = System.Array.Empty<PlanFrameItem>();
        if (!toolPart.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return false;
        if (!input.TryGetProperty("todos", out var todos) || todos.ValueKind != JsonValueKind.Array) return false;

        var list = new List<PlanFrameItem>();
        foreach (var todo in todos.EnumerateArray())
        {
            if (todo.ValueKind != JsonValueKind.Object) continue;
            var content = todo.TryGetProperty("content", out var c) ? c.GetString() : null;
            if (string.IsNullOrWhiteSpace(content)) continue;
            var status = todo.TryGetProperty("status", out var s) ? s.GetString() : null;
            list.Add(new PlanFrameItem(PlanItemId.From(content), content!.Trim(), PlanItemStatus.Normalize(status)));
        }
        if (list.Count == 0) return false;
        items = list;
        return true;
    }

    private static string? ExtractToolArgument(JsonElement toolPart)
    {
        if (!toolPart.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) return null;
        // Common tool argument keys we care about for the typed event:
        foreach (var key in new[] { "file_path", "path", "command", "pattern", "url", "query" })
        {
            if (input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                return string.IsNullOrWhiteSpace(s) ? null : s;
            }
        }
        return null;
    }

    private static string? ExtractFirstLine(JsonElement toolResultPart)
    {
        if (!toolResultPart.TryGetProperty("content", out var c)) return null;
        string? text = null;
        if (c.ValueKind == JsonValueKind.String) text = c.GetString();
        else if (c.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in c.EnumerateArray())
            {
                if (p.TryGetProperty("type", out var pt) && pt.GetString() == "text"
                    && p.TryGetProperty("text", out var tx))
                {
                    text = tx.GetString();
                    break;
                }
            }
        }
        if (string.IsNullOrEmpty(text)) return null;
        var idx = text!.IndexOf('\n');
        return idx >= 0 ? text[..idx] : text;
    }

    private static string FormatUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
        var output = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
        var cacheRead = usage.TryGetProperty("cache_read_input_tokens", out var cr) && cr.TryGetInt64(out var crv) ? crv : 0;
        return $"input={input} output={output} cache_read={cacheRead}";
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];
}

using System.Text.Json;
using CodingAgentRunner.Events;

namespace CodingAgentRunner.Adapters;

/// <summary>
/// Maps Codex's <c>codex exec --json</c> JSONL output onto the typed
/// <see cref="CliRunEvent"/> contract. The frame catalogue is intentionally
/// closer to the App Server protocol than to the legacy <c>session_meta</c> shape
/// — a future migration to the App Server transport keeps this mapping.
///
/// <para>
/// Real frames (verified against <c>codex 0.128.0</c>):
/// </para>
/// <list type="bullet">
///   <item><c>thread.started</c> with <c>thread_id</c> &#8594; <see cref="CliRunEvent.SessionStarted"/>.</item>
///   <item><c>turn.started</c> &#8594; <see cref="CliRunEvent.TurnStarted"/>.</item>
///   <item><c>item.completed</c> with <c>item.type=agent_message</c> &#8594; <see cref="CliRunEvent.OutputDelta"/> (carries <c>item.text</c>).</item>
///   <item><c>item.completed</c> with <c>item.type=command_call</c> / <c>file_change</c> / etc. &#8594; <see cref="CliRunEvent.ToolCompleted"/>.</item>
///   <item><c>item.started</c> when applicable &#8594; <see cref="CliRunEvent.ToolStarted"/>.</item>
///   <item><c>item.started</c> / <c>item.completed</c> with <c>item.type=reasoning</c> &#8594; <see cref="CliRunEvent.Heartbeat"/> (liveness ping; see <see cref="IsReasoningItem"/>).</item>
///   <item><c>turn.completed</c> with <c>usage</c> &#8594; <see cref="CliRunEvent.TurnCompleted"/> — the CLI's real completion signal.</item>
///   <item><c>turn.failed</c> &#8594; <see cref="CliRunEvent.TurnFailed"/>.</item>
///   <item><c>session_meta</c> (legacy shape) &#8594; <see cref="CliRunEvent.SessionStarted"/>.</item>
///   <item><c>token_count</c> with <c>rate_limits</c> (core-protocol shape) &#8594; one
///     <see cref="CliRunEvent.RateLimitObserved"/> per window, with the precise
///     <c>used_percent</c>. As of codex 0.142 the <c>exec --json</c> stream does NOT
///     emit this frame (it lives in the rollout logs and the app-server protocol);
///     the mapping serves rollout replay and future stream versions.</item>
///   <item>everything else &#8594; <see cref="CliRunEvent.Unknown"/>.</item>
/// </list>
/// </summary>
public static class CodexEventAdapter
{
    /// <summary>Map one <c>codex exec --json</c> line to zero or more <see cref="CliRunEvent"/> instances.</summary>
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
            case "thread.started":
            {
                var id = root.TryGetProperty("thread_id", out var tid) ? tid.GetString() : null;
                yield return new CliRunEvent.SessionStarted(id) { RunId = runId };
                yield break;
            }
            case "session_meta":
            {
                // Legacy shape; same semantic meaning as thread.started.
                var id = root.TryGetProperty("session_id", out var sid) ? sid.GetString() : null;
                yield return new CliRunEvent.SessionStarted(id) { RunId = runId };
                yield break;
            }
            case "turn.started":
                yield return new CliRunEvent.TurnStarted { RunId = runId };
                yield break;
            case "turn.completed":
            {
                string? usage = null;
                if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
                    usage = FormatUsage(u);
                yield return new CliRunEvent.TurnCompleted(usage) { RunId = runId };
                yield break;
            }
            case "turn.failed":
            {
                var reason = root.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                    ? (m.GetString() ?? "error")
                    : "error";
                yield return new CliRunEvent.TurnFailed(reason) { RunId = runId };
                yield break;
            }
            case "token_count":
            {
                foreach (var evt in MapRateLimits(root, runId)) yield return evt;
                yield break;
            }
            case "item.started":
            {
                if (TryExtractPlan(root, out var startItems))
                    yield return new CliRunEvent.PlanUpdated("codex/update_plan", startItems) { RunId = runId };
                if (IsReasoningItem(root))
                {
                    yield return new CliRunEvent.Heartbeat { RunId = runId };
                    yield break;
                }
                var (kind, name, arg) = ClassifyItem(root);
                if (kind == "tool")
                    yield return new CliRunEvent.ToolStarted(name, arg) { RunId = runId };
                yield break;
            }
            case "item.completed":
            {
                if (TryExtractPlan(root, out var doneItems))
                    yield return new CliRunEvent.PlanUpdated("codex/update_plan", doneItems) { RunId = runId };
                if (IsReasoningItem(root))
                {
                    yield return new CliRunEvent.Heartbeat { RunId = runId };
                    yield break;
                }
                var (kind, name, arg) = ClassifyItem(root);
                if (kind == "agent_message")
                {
                    if (!string.IsNullOrEmpty(arg))
                        yield return new CliRunEvent.OutputDelta(arg!) { RunId = runId };
                }
                else if (kind == "tool")
                {
                    yield return new CliRunEvent.ToolCompleted(name, IsError: false, FirstLine: Truncate(arg, 200))
                    { RunId = runId };
                }
                yield break;
            }
            default:
                yield return new CliRunEvent.Unknown(Truncate(jsonLine, 200) ?? "") { RunId = runId };
                yield break;
        }
    }

    /// <summary>
    /// True when the frame's nested <c>item</c> is a Codex <c>reasoning</c> block.
    /// Codex at higher reasoning efforts (notably <c>xhigh</c>) thinks silently for
    /// minutes — emitting only reasoning items — before its first turn frame, so the
    /// run sits in <see cref="RunPhase.PromptConsumed"/> producing no
    /// <see cref="CliRunEvent.OutputDelta"/>. A reasoning item is therefore mapped
    /// to a <see cref="CliRunEvent.Heartbeat"/> liveness ping (it resets the
    /// watchdog silence clock) rather than to a phantom
    /// <c>ToolStarted</c>/<c>ToolCompleted</c> for a "reasoning" tool, which would
    /// both pollute the tool log and mis-advance the phase to
    /// <see cref="RunPhase.ToolExecuting"/>.
    /// </summary>
    private static bool IsReasoningItem(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return false;
        var itemType = item.TryGetProperty("type", out var ity) ? ity.GetString() : null;
        return string.Equals(itemType, "reasoning", StringComparison.Ordinal);
    }

    /// <summary>
    /// Map a Codex item frame's nested <c>item</c> object onto a normalized
    /// (kind, name, argument) triple. <c>kind</c> is one of <c>agent_message</c>,
    /// <c>tool</c>, or <c>other</c>.
    /// </summary>
    private static (string Kind, string Name, string? Argument) ClassifyItem(JsonElement root)
    {
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object)
            return ("other", "item", null);

        var itemType = item.TryGetProperty("type", out var ity) ? ity.GetString() : null;
        if (string.Equals(itemType, "agent_message", StringComparison.Ordinal))
        {
            var text = item.TryGetProperty("text", out var tx) ? tx.GetString() : null;
            return ("agent_message", "agent_message", text);
        }

        // Tool-like items: command_call, file_change, web_search, etc.
        // Fall back to the type string as the tool name.
        if (!string.IsNullOrEmpty(itemType))
        {
            string? arg = null;
            foreach (var key in new[] { "command", "file_path", "path", "query", "url" })
            {
                if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
                {
                    arg = v.GetString();
                    break;
                }
            }
            return ("tool", itemType!, arg);
        }
        return ("other", "item", null);
    }

    /// <summary>
    /// Detect a Codex <c>update_plan</c> item and pull its plan steps. The item
    /// shape is <c>{"item":{"type":"update_plan","plan":[{"step","status"}]}}</c>.
    /// Tolerant of <c>content</c> as an alias for <c>step</c>. Returns false for any
    /// non-plan item so the normal tool-call path is unaffected.
    /// </summary>
    private static bool TryExtractPlan(JsonElement root, out IReadOnlyList<PlanFrameItem> items)
    {
        items = System.Array.Empty<PlanFrameItem>();
        if (!root.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return false;
        var itemType = item.TryGetProperty("type", out var ity) ? ity.GetString() : null;
        if (!string.Equals(itemType, "update_plan", StringComparison.Ordinal)) return false;
        if (!item.TryGetProperty("plan", out var plan) || plan.ValueKind != JsonValueKind.Array) return false;

        var list = new List<PlanFrameItem>();
        foreach (var entry in plan.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            var title = entry.TryGetProperty("step", out var st) ? st.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) && entry.TryGetProperty("content", out var ct)) title = ct.GetString();
            if (string.IsNullOrWhiteSpace(title)) continue;
            var status = entry.TryGetProperty("status", out var ss) ? ss.GetString() : null;
            list.Add(new PlanFrameItem(PlanItemId.From(title), title!.Trim(), PlanItemStatus.Normalize(status)));
        }
        if (list.Count == 0) return false;
        items = list;
        return true;
    }

    /// <summary>
    /// Map a core-protocol <c>token_count</c> frame's <c>rate_limits</c> object onto
    /// one <see cref="CliRunEvent.RateLimitObserved"/> per window. Window labels use
    /// the same minutes-based vocabulary as the built-in Codex quota probe
    /// (300&#160;min &#8594; <c>5-hour</c>, 10&#160;080&#160;min &#8594; <c>weekly</c>) so
    /// <see cref="Quota.QuotaService.Observe"/> merges events and probe results into
    /// the same <see cref="Quota.QuotaWindow"/>. A frame with a null / absent
    /// <c>rate_limits</c> maps to nothing (known codex behaviour in some sessions).
    /// </summary>
    private static IEnumerable<CliRunEvent> MapRateLimits(JsonElement root, string runId)
    {
        if (!root.TryGetProperty("rate_limits", out var rl) || rl.ValueKind != JsonValueKind.Object)
            yield break;

        var reached = rl.TryGetProperty("rate_limit_reached_type", out var rt) && rt.ValueKind == JsonValueKind.String
            ? rt.GetString()
            : null;

        foreach (var property in new[] { "primary", "secondary" })
        {
            if (!rl.TryGetProperty(property, out var window) || window.ValueKind != JsonValueKind.Object) continue;

            double? minutes = window.TryGetProperty("window_minutes", out var wm) && wm.TryGetDouble(out var m) ? m : null;
            double? usedPercent = window.TryGetProperty("used_percent", out var up) && up.TryGetDouble(out var pct) ? pct : null;
            var resetsAt = window.TryGetProperty("resets_at", out var ra) && ra.TryGetInt64(out var reset) ? reset : 0L;

            yield return new CliRunEvent.RateLimitObserved(
                Window: Quota.CodexSessionLogProbe.WindowLabel(minutes),
                Status: reached is null ? "allowed" : $"reached:{reached}",
                ResetsAt: resetsAt,
                OverageStatus: null,
                IsUsingOverage: false,
                UsedPercent: usedPercent) { RunId = runId };
        }
    }

    private static string FormatUsage(JsonElement usage)
    {
        var input = usage.TryGetProperty("input_tokens", out var i) && i.TryGetInt64(out var iv) ? iv : 0;
        var cached = usage.TryGetProperty("cached_input_tokens", out var c) && c.TryGetInt64(out var cv) ? cv : 0;
        var output = usage.TryGetProperty("output_tokens", out var o) && o.TryGetInt64(out var ov) ? ov : 0;
        var reasoning = usage.TryGetProperty("reasoning_output_tokens", out var r) && r.TryGetInt64(out var rv) ? rv : 0;
        return $"input={input} cached={cached} output={output} reasoning={reasoning}";
    }

    private static string? Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return s;
        return s!.Length <= max ? s : s[..max];
    }
}

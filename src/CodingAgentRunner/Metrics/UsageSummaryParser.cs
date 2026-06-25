namespace CodingAgentRunner.Metrics;

/// <summary>Typed per-turn token counts parsed from an adapter's free-form usage summary.</summary>
/// <param name="Input">Fresh (non-cached) input tokens.</param>
/// <param name="Output">Generated output tokens.</param>
/// <param name="Cached">Cached input tokens (Claude <c>cache_read</c> / Codex+Gemini <c>cached</c>).</param>
/// <param name="Reasoning">Extended-reasoning output tokens (Codex), 0 when not reported.</param>
public readonly record struct UsageTokens(long Input, long Output, long Cached, long Reasoning);

/// <summary>
/// Parses the one-line, free-form usage summary each adapter emits on
/// <see cref="Events.CliRunEvent.TurnCompleted"/> into typed token counts. Tolerant of
/// the per-CLI vocabulary differences: Claude reports <c>cache_read=</c> while Codex /
/// Gemini report <c>cached=</c> (both fold to <see cref="UsageTokens.Cached"/>); unknown
/// keys (e.g. Gemini's <c>tool_calls=</c>) are ignored. Never throws.
/// </summary>
public static class UsageSummaryParser
{
    /// <summary>Parse a usage summary; returns all-zero for null/empty/garbage input.</summary>
    public static UsageTokens Parse(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return default;

        long input = 0, output = 0, cached = 0, reasoning = 0;
        foreach (var token in summary.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = token.IndexOf('=');
            if (eq <= 0) continue;
            if (!long.TryParse(token.AsSpan(eq + 1), out var n)) continue;
            switch (token[..eq])
            {
                case "input": input = n; break;
                case "output": output = n; break;
                case "cache_read":
                case "cached": cached = n; break;
                case "reasoning": reasoning = n; break;
            }
        }
        return new UsageTokens(input, output, cached, reasoning);
    }
}

using CodingAgentRunner.Abstractions;

namespace CodingAgentRunner.Quota;

internal static class QuotaWaitPolicy
{
    internal static DateTime? SelectReset(
        QuotaSnapshot? snapshot,
        WaitOnQuotaOptions options,
        DateTime now)
    {
        if (!options.Enabled || options.QuotaService is null || options.Threshold <= TimeSpan.Zero)
            return null;
        if (snapshot is null || !string.IsNullOrWhiteSpace(snapshot.Error))
            return null;

        return snapshot.Windows
            .Select(window => window.ResetAt)
            .Where(reset => reset is not null && reset.Value > now && reset.Value - now <= options.Threshold)
            .Select(reset => reset!.Value)
            .OrderBy(reset => reset)
            .FirstOrDefault() is { } selected && selected != default ? selected : null;
    }

    internal static bool IsQuotaLimitFailure(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return false;
        return reason.Contains("rate limit exceeded", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("usage limit", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("quota exceeded", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("quota exhausted", StringComparison.OrdinalIgnoreCase);
    }
}

namespace CodingAgentRunner.Model;

/// <summary>
/// Converts a normalized thinking / reasoning level into the concrete CLI args
/// that select it. Unsupported CLI/model combinations return no flags so the CLI
/// default wins.
/// </summary>
public static class CliReasoningFlags
{
    /// <summary>The reasoning-effort flags for the given CLI + model + requested level.</summary>
    public static IReadOnlyList<string> For(string? cliType, string? model, string? thinkingLevel)
    {
        var cli = CliTypes.Normalize(cliType);
        var normalized = CliThinkingLevels.Normalize(cli, model, thinkingLevel);
        if (normalized is null) return [];

        if (string.Equals(cli, CliTypes.Claude, StringComparison.OrdinalIgnoreCase))
            return ["--effort", normalized];

        if (string.Equals(cli, CliTypes.Codex, StringComparison.OrdinalIgnoreCase))
            return ["-c", $"model_reasoning_effort=\"{normalized}\""];

        return [];
    }
}

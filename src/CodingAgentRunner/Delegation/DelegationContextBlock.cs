using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodingAgentRunner.Delegation;

/// <summary>
/// Appends the delegation rule and the run's agent inventory to the prompt. The
/// agent files alone change nothing a primary agent can act on deliberately — the
/// CLI would have to infer the opportunity. This block states the rule and lists
/// what is available, in the same shape as the chat-attachment block.
/// </summary>
public static class DelegationContextBlock
{
    // 2200-2204 belong to SubagentMaterializer / SubagentMaterialization; this is the
    // next free id in the delegation block. Two event names sharing one id would make
    // the pair indistinguishable to a consumer filtering by EventId.
    private static readonly EventId Injected = new(2205, "DelegationContextInjected");

    /// <summary>Opening tag of the injected block; also how a consumer can find it in a logged prompt.</summary>
    public const string OpenTag = "<delegation-economy>";

    /// <summary>Closing tag of the injected block.</summary>
    public const string CloseTag = "</delegation-economy>";

    /// <summary>
    /// The rule the runner injects when the project supplies none. Kept short on
    /// purpose: it competes with the task prompt for attention.
    /// </summary>
    public const string DefaultRule =
        """
        Delegate simple, fully specified subtasks to one of the cheap subagents listed below
        instead of doing them in this thread. A subagent runs on a smaller model and its
        intermediate output never enters your context, so a delegated sweep costs a fraction
        of the same work done here.

        Delegate when the subtask is mechanical and the result has a checkable shape: repo-wide
        greps and inventories, log scans, find/replace sweeps, confirming that a claim holds.
        Keep in this thread anything that needs design judgement, reasoning about intent across
        files, or a decision you have to defend.

        Give a subagent one precise instruction and state the exact shape of the answer you want
        back. Check what it returns before you build on it.
        """;

    /// <summary>
    /// Build the prompt the CLI should receive: <paramref name="prompt"/> followed by
    /// the delegation block. Returns null when nothing should be injected (the option
    /// is off, or no agent is available) so the caller leaves the prompt alone.
    /// </summary>
    public static string? Compose(
        string prompt,
        SubagentMaterialization materialization,
        string workingDirectory,
        DelegationOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.InjectContextBlock) return null;
        if (materialization.Available.Count == 0) return null;

        var (rule, ruleSource) = ReadRule(workingDirectory, options, logger);

        var lines = new List<string>(materialization.Available.Count + 8)
        {
            prompt,
            "",
            OpenTag,
            rule,
            "",
            "Subagents available for this run:",
        };

        foreach (var agent in materialization.Available)
        {
            lines.Add(JsonSerializer.Serialize(new
            {
                name = agent.Name,
                model = agent.Model,
                use_for = OneLine(agent.Description),
            }));
        }

        lines.Add(CloseTag);

        logger?.LogInformation(
            Injected,
            "Injected the delegation context block for {Cli}: {AgentCount} agent(s), rule from {RuleSource}",
            materialization.CliType,
            materialization.Available.Count,
            ruleSource);

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Read the project's rule text when it committed one, else the built-in rule.
    /// The project file wins so a repository can phrase the economy rule its own way
    /// without any runner configuration.
    /// </summary>
    private static (string Rule, string Source) ReadRule(string workingDirectory, DelegationOptions options, ILogger? logger)
    {
        var relative = options.ContextBlockRelativePath;
        if (string.IsNullOrWhiteSpace(relative) || string.IsNullOrWhiteSpace(workingDirectory))
            return (DefaultRule, "the runner default");

        string path;
        try { path = Path.GetFullPath(Path.Combine(workingDirectory, relative)); }
        catch { return (DefaultRule, "the runner default"); }

        try
        {
            if (!File.Exists(path)) return (DefaultRule, "the runner default");
            var text = File.ReadAllText(path).Trim();
            return text.Length == 0
                ? (DefaultRule, "the runner default")
                : (text, path);
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "Could not read the delegation rule at {Path}; using the runner default", path);
            return (DefaultRule, "the runner default");
        }
    }

    private static string? OneLine(string? value)
        => value?.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

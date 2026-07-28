using CodingAgentRunner.Model;

namespace CodingAgentRunner.Delegation;

/// <summary>
/// One cheap worker the primary agent may delegate a subtask to, declared as data.
/// The runner materializes it into the CLI's own agent-definition convention before
/// a run (Claude <c>.claude/agents/&lt;name&gt;.md</c>, Codex
/// <c>.codex/agents/&lt;name&gt;.toml</c>) — there is no spawn API in this library, the
/// CLI does the spawning.
/// <para>
/// <see cref="Description"/> is the field that actually drives delegation: both CLIs
/// pick a subagent by matching the task at hand against it, so write it as "use this
/// for X", not as a title.
/// </para>
/// </summary>
public sealed record SubagentDefinition
{
    /// <summary>
    /// Agent id and file stem. Lower-case letters, digits and dashes — it becomes a
    /// file name, so anything else is rejected by <see cref="IsValidName"/>.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>What this agent is for. The CLI matches a subtask against this text when choosing an agent.</summary>
    public required string Description { get; init; }

    /// <summary>The agent's own system prompt (Claude file body / Codex <c>developer_instructions</c>).</summary>
    public required string Instructions { get; init; }

    /// <summary>Model for Claude Code — an alias (<c>haiku</c>, <c>sonnet</c>, <c>opus</c>, <c>inherit</c>) or a full model id. Null leaves the CLI default.</summary>
    public string? ClaudeModel { get; init; }

    /// <summary>Model for Codex — a model slug such as <c>gpt-5.6-terra</c>. Null leaves the CLI default.</summary>
    public string? CodexModel { get; init; }

    /// <summary>Optional tool allowlist for the agent. Claude writes it to the <c>tools</c> frontmatter key; Codex ignores it.</summary>
    public IReadOnlyList<string>? Tools { get; init; }

    /// <summary>The model this agent should run on under <paramref name="cliType"/>, or null for the CLI's default.</summary>
    public string? ModelFor(string cliType) => cliType switch
    {
        CliTypes.Claude => Blank(ClaudeModel) ? null : ClaudeModel,
        CliTypes.Codex => Blank(CodexModel) ? null : CodexModel,
        _ => null,
    };

    /// <summary>
    /// Whether <paramref name="name"/> is usable as an agent id: non-empty, lower-case
    /// alphanumerics and dashes, no path separators. Checked before the runner writes
    /// anything, so a bad definition can never escape the agents directory.
    /// </summary>
    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name!.Length > 64) return false;
        foreach (var c in name)
            if (!char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-')
                return false;
        return name[0] != '-' && name[^1] != '-';
    }

    private static bool Blank(string? s) => string.IsNullOrWhiteSpace(s);
}

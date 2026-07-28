namespace CodingAgentRunner.Delegation;

/// <summary>
/// Run configuration for cheap-subagent delegation: which agent definitions the
/// runner materializes into a run's workspace, and whether the prompt tells the
/// primary agent that they exist. There is no spawn API — the CLI already knows how
/// to run a subagent; this only supplies the definitions and the rule.
/// <para>
/// Enabled by default. Everything about it is convention, not per-environment
/// settings: a project overrides one agent by committing a file with the same name,
/// and opts out of the runner's set entirely with an
/// <see cref="SubagentMaterializer.OptOutFileName"/> file in its agents directory.
/// </para>
/// </summary>
public sealed record DelegationOptions
{
    /// <summary>
    /// Whether the runner materializes agent definitions and injects the delegation
    /// rule. Default: true. False leaves the workspace untouched and the prompt
    /// unchanged — a project's own committed agents still work, because those are
    /// read by the CLI, not by this library.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The agent set to materialize. Defaults to <see cref="SubagentDefaults.All"/>.</summary>
    public IReadOnlyList<SubagentDefinition> Agents { get; init; } = SubagentDefaults.All;

    /// <summary>
    /// Whether the run prompt gets the <c>&lt;delegation-economy&gt;</c> block listing
    /// the available agents and the rule for using them. Default: true — a capability
    /// the primary agent is never told about saves nothing.
    /// </summary>
    public bool InjectContextBlock { get; init; } = true;

    /// <summary>
    /// Working-directory-relative path of an optional project-supplied rule text. When
    /// that file exists its content replaces the built-in rule inside the injected
    /// block; the agent inventory is appended either way. Default:
    /// <c>contexts/delegation-economy.md</c>.
    /// </summary>
    public string ContextBlockRelativePath { get; init; } = "contexts/delegation-economy.md";
}

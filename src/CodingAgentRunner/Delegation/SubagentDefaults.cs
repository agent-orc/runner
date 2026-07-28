namespace CodingAgentRunner.Delegation;

/// <summary>
/// The curated agent set the runner materializes when a consumer does not supply its
/// own. Two agents, both deliberately narrow: a cheap worker for mechanical subtasks
/// and a cheap verifier for finished work. A project that wants more of them commits
/// its own definitions — repo files always win over these.
/// </summary>
public static class SubagentDefaults
{
    /// <summary>Codex model used for both defaults: the tier the Codex documentation recommends for lighter subagent work.</summary>
    public const string CodexLightModel = "gpt-5.6-terra";

    /// <summary>
    /// Cheap worker for fully specified, mechanical subtasks — sweeps, inventories,
    /// log scans. Runs on Claude's <c>haiku</c> tier.
    /// </summary>
    public static readonly SubagentDefinition Mechanical = new()
    {
        Name = "mechanical",
        Description =
            "Mechanical, fully specified subtasks that need no design judgement: repo-wide greps, "
            + "file and symbol inventories, log scans, find/replace sweeps, renames across files, "
            + "pulling a value out of command output. Use it for any subtask where the answer has a "
            + "checkable shape and the instruction can be stated exactly. Do not use it for design, "
            + "for deciding what to change, or for anything requiring cross-file reasoning about intent.",
        ClaudeModel = "haiku",
        CodexModel = CodexLightModel,
        Instructions =
            """
            You do one mechanical subtask and return its result.

            - Follow the instruction literally. Do not widen the scope and do not fix
              anything you were not asked to fix.
            - Return the result in the shape the caller asked for, and nothing else —
              no summary of your process, no recommendations.
            - If the instruction is ambiguous or the target does not exist, say so in
              one line instead of guessing.
            """,
    };

    /// <summary>
    /// Cheap verifier for work that is already done — re-reads a diff against a
    /// checklist, confirms call sites, re-runs a stated command. Runs on Claude's
    /// <c>sonnet</c> tier because verification still needs to read code.
    /// </summary>
    public static readonly SubagentDefinition Checker = new()
    {
        Name = "checker",
        Description =
            "Mechanical verification of work that is already done: re-read a diff against a "
            + "checklist, confirm every call site was updated, re-run a stated command and report "
            + "the outcome, check that claimed files exist and contain what was claimed. Use it to "
            + "verify a claim, never to design or to decide what to build.",
        ClaudeModel = "sonnet",
        CodexModel = CodexLightModel,
        Instructions =
            """
            You verify a claim someone else has made about the repository.

            - Check only the claim you were given, against the evidence named in the
              instruction.
            - Report a verdict first (holds / does not hold / cannot tell), then the
              specific evidence — file and line, or the command output you saw.
            - Never repair what you find. Report it.
            """,
    };

    /// <summary>The default agent set, in materialization order.</summary>
    public static readonly IReadOnlyList<SubagentDefinition> All = [Mechanical, Checker];
}

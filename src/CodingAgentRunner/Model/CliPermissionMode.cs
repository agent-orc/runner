namespace CodingAgentRunner.Model;

/// <summary>
/// Permission / sandbox modes a run can pick per CLI. These are the platform's
/// own vocabulary; <see cref="CliPermissionFlags"/> maps each (cliType, mode)
/// pair to the concrete command-line flags for that CLI.
/// <para>
/// <b>YOLO is the default for every CLI</b> because the runner drives CLIs
/// unattended: any interactive permission / sandbox / folder-trust prompt would
/// hang the run forever. The other modes exist for consumers who understand the
/// trade-off and want a tighter envelope on a specific run.
/// </para>
/// </summary>
public static class CliPermissionModes
{
    /// <summary>Maximum autonomy: skip every permission / sandbox / trust prompt.</summary>
    public const string Yolo = "yolo";

    /// <summary>Auto-approve edits inside the workspace, but not arbitrary commands / network.</summary>
    public const string WorkspaceWrite = "workspace-write";

    /// <summary>Read-only / plan posture: the agent may inspect but not freely mutate.</summary>
    public const string ReadOnly = "read-only";

    /// <summary>
    /// Inject no permission flag — defer entirely to the CLI's own global config
    /// (<c>~/.codex/config.toml</c>, <c>~/.claude/settings.json</c>, …). This is
    /// the escape hatch for consumers who manage permissions globally.
    /// </summary>
    public const string Custom = "custom";

    /// <summary>All permission modes.</summary>
    public static readonly string[] All = [Yolo, WorkspaceWrite, ReadOnly, Custom];

    /// <summary>True when <paramref name="mode"/> is a known permission mode.</summary>
    public static bool IsValid(string? mode)
        => !string.IsNullOrWhiteSpace(mode)
           && All.Contains(mode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonicalize a mode id; unknown / empty values fall back to <see cref="Yolo"/>.</summary>
    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return Yolo;
        var v = mode.Trim();
        foreach (var m in All)
            if (string.Equals(m, v, StringComparison.OrdinalIgnoreCase))
                return m;
        return Yolo;
    }

    /// <summary>Short human label for the UI / probe responses.</summary>
    public static string DisplayName(string? mode) => Normalize(mode) switch
    {
        WorkspaceWrite => "Workspace-Write",
        ReadOnly => "Read-Only",
        Custom => "Custom (global config)",
        _ => "YOLO",
    };
}

/// <summary>
/// Pure mapper from a (cliType, permission-mode) pair to the command-line flags
/// the driver must inject. Side-effect and dependency free so it is directly
/// unit-testable.
/// <para>
/// Each CLI exposes a different permission vocabulary; where a CLI has no
/// faithful equivalent for a mode, the closest safe approximation is used.
/// </para>
/// </summary>
public static class CliPermissionFlags
{
    /// <summary>
    /// Returns the flags to append to the CLI invocation for the given mode. An
    /// empty list means "inject nothing" (defer to the CLI's global config).
    /// Unknown cliType / mode normalize to their defaults (YOLO).
    /// </summary>
    public static IReadOnlyList<string> For(string? cliType, string? mode)
    {
        var m = CliPermissionModes.Normalize(mode);
        return CliTypes.Normalize(cliType) switch
        {
            CliTypes.Claude => Claude(m),
            CliTypes.Codex => Codex(m),
            CliTypes.Gemini => Gemini(m),
            CliTypes.Copilot => Copilot(m),
            _ => Claude(m),
        };
    }

    private static IReadOnlyList<string> Claude(string mode) => mode switch
    {
        // --dangerously-skip-permissions bypasses every tool-permission prompt.
        CliPermissionModes.Yolo => ["--dangerously-skip-permissions"],
        // acceptEdits auto-approves file edits but still gates other tools.
        CliPermissionModes.WorkspaceWrite => ["--permission-mode", "acceptEdits"],
        // plan mode is the closest read-only posture: Claude inspects + plans
        // without applying edits.
        CliPermissionModes.ReadOnly => ["--permission-mode", "plan"],
        _ => [],
    };

    private static IReadOnlyList<string> Codex(string mode) => mode switch
    {
        CliPermissionModes.Yolo => ["--sandbox", "danger-full-access"],
        CliPermissionModes.WorkspaceWrite => ["--sandbox", "workspace-write"],
        CliPermissionModes.ReadOnly => ["--sandbox", "read-only"],
        _ => [],
    };

    private static IReadOnlyList<string> Gemini(string mode)
    {
        // --skip-trust is orthogonal to the permission posture: it dismisses the
        // "Do you trust this folder?" modal that would otherwise hang ANY
        // unattended run. It is therefore always injected (even for read-only /
        // custom) so the CLI never blocks on the trust dialog.
        return mode switch
        {
            // -y == --approval-mode yolo. Kept as the historic flag pair so the
            // YOLO path is byte-for-byte what the driver shipped before.
            CliPermissionModes.Yolo => ["--skip-trust", "-y"],
            CliPermissionModes.WorkspaceWrite => ["--skip-trust", "--approval-mode", "auto_edit"],
            CliPermissionModes.ReadOnly => ["--skip-trust", "--approval-mode", "default"],
            _ => ["--skip-trust"],
        };
    }

    private static IReadOnlyList<string> Copilot(string mode) => mode switch
    {
        // Copilot's headless flag surface is all-or-nothing: --allow-all greenlights
        // every tool. There is no granular workspace-write / read-only preset, so
        // only YOLO injects a flag; other modes fall back to Copilot's interactive
        // defaults (which will prompt and therefore stall an unattended run).
        CliPermissionModes.Yolo => ["--allow-all"],
        _ => [],
    };
}

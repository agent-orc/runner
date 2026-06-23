namespace CodingAgentRunner.Model;

/// <summary>
/// Whether a run sees a <b>clean</b> (isolated, freshly seeded) persistent CLI
/// state or the operator's <b>shared</b> global state.
/// <para>
/// <b>CLEAN is the default for coding runs</b>: the agent sees only the prompt
/// plus the versioned repo files, so a run is reproducible and free of leftover
/// session history, accumulated memory, or one-off settings from earlier runs.
/// "clean" is <i>not</i> a CLI flag — each adapter implements it via the CLI's own
/// home / config-dir env var (Claude <c>CLAUDE_CONFIG_DIR</c>, Codex
/// <c>CODEX_HOME</c>), seeding only the auth + base config into a per-run temp
/// directory that is torn down when the run's tracking entry is evicted. CLIs that
/// expose no such redirect are honestly <see cref="SupportsClean"/> == false and
/// always run shared.
/// </para>
/// <para>
/// Repo instruction files (<c>AGENTS.md</c> / <c>CLAUDE.md</c> in the working
/// tree) stay active in <b>both</b> modes — they are loaded from the checkout, not
/// from the CLI home, so clean mode never hides them.
/// </para>
/// </summary>
public static class CliContextModes
{
    /// <summary>Isolated per-run persistent state: only prompt + versioned repo files. Default.</summary>
    public const string Clean = "clean";

    /// <summary>The operator's shared global CLI state (session history, memory, settings).</summary>
    public const string Shared = "shared";

    /// <summary>All context modes.</summary>
    public static readonly string[] All = [Clean, Shared];

    /// <summary>True when <paramref name="mode"/> is a known context mode.</summary>
    public static bool IsValid(string? mode)
        => !string.IsNullOrWhiteSpace(mode)
           && All.Contains(mode, StringComparer.OrdinalIgnoreCase);

    /// <summary>Canonicalize a mode id; unknown / empty values fall back to <see cref="Clean"/>.</summary>
    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode)) return Clean;
        var v = mode.Trim();
        foreach (var m in All)
            if (string.Equals(m, v, StringComparison.OrdinalIgnoreCase))
                return m;
        return Clean;
    }

    /// <summary>Short human label for the UI / panel.</summary>
    public static string DisplayName(string? mode) => Normalize(mode) switch
    {
        Shared => "Shared",
        _ => "Clean",
    };

    /// <summary>
    /// Whether the adapter for <paramref name="cliType"/> can actually isolate
    /// persistent state for a clean run. Claude (<c>CLAUDE_CONFIG_DIR</c>) and Codex
    /// (<c>CODEX_HOME</c>) redirect their whole config home to a per-run temp dir;
    /// Copilot and Gemini expose no such redirect, so they are shared-only — a clean
    /// selection is honoured as a no-op (still shared). This is the single source of
    /// truth shared by the adapters and any settings UI so they can never disagree.
    /// </summary>
    public static bool SupportsClean(string? cliType) => CliTypes.Normalize(cliType) switch
    {
        CliTypes.Claude => true,
        CliTypes.Codex => true,
        _ => false,
    };
}

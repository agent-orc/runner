namespace CodingAgentRunner.Execution;

/// <summary>
/// A CLI's clean-context recipe, declared as data on its <see cref="CliDescriptor"/>:
/// which environment variable redirects the CLI's config home, which user config dir
/// it seeds from, which files stay linked to their source, and which files are copied.
/// The engine turns this into the per-run isolated home at spawn time. Declaring it
/// (rather than running it) keeps the descriptor a pure value and the isolation
/// mechanics in the engine.
/// </summary>
/// <param name="EnvVar">The env var that points the CLI at the isolated home (e.g. <c>CLAUDE_CONFIG_DIR</c>, <c>CODEX_HOME</c>).</param>
/// <param name="SourceConfigDirName">The user-home-relative config dir to seed from (e.g. <c>.claude</c>, <c>.codex</c>).</param>
/// <param name="LinkedSeedFiles">Files that must share in-place updates with the source, created as a hardlink, symlink, or copy fallback.</param>
/// <param name="CopiedSeedFiles">Files copied into the clean home so changes remain isolated from the source.</param>
public sealed record CleanContextSpec(
    string EnvVar,
    string SourceConfigDirName,
    IReadOnlyList<string> LinkedSeedFiles,
    IReadOnlyList<string> CopiedSeedFiles)
{
    /// <summary>
    /// Create a recipe whose seed files use the default link-first behavior.
    /// </summary>
    public CleanContextSpec(
        string envVar,
        string sourceConfigDirName,
        IReadOnlyList<string> seedFiles)
        : this(envVar, sourceConfigDirName, seedFiles, Array.Empty<string>())
    {
    }
}

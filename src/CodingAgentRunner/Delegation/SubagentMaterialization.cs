using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Delegation;

/// <summary>Where one agent available to a run came from.</summary>
public static class SubagentSources
{
    /// <summary>The runner created this definition file for the run and removes it when the run ends.</summary>
    public const string Runner = "runner";

    /// <summary>
    /// The definition was already in the workspace when the run started — committed by
    /// the project, or left behind by an earlier run. The runner never writes,
    /// overwrites, or deletes it.
    /// </summary>
    public const string Repo = "repo";
}

/// <summary>One agent the primary agent can delegate to during a run.</summary>
/// <param name="Name">Agent id (the file stem).</param>
/// <param name="Description">What the agent is for — the text the CLI matches a subtask against.</param>
/// <param name="Model">Model the agent runs on, when the runner set it; null for a repo-provided definition or a CLI default.</param>
/// <param name="Source">One of <see cref="SubagentSources"/>.</param>
/// <param name="Path">Absolute path of the definition file.</param>
public sealed record SubagentAvailability(
    string Name,
    string? Description,
    string? Model,
    string Source,
    string Path);

/// <summary>
/// One run's materialized agent set: the definition files the runner created in the
/// workspace plus the ones it found already there. Owned by the run — disposing it
/// deletes <em>only</em> the files this run itself created, so a project's own agent
/// definitions survive untouched.
/// </summary>
public sealed class SubagentMaterialization : IDisposable
{
    private static readonly EventId ReplacedBeforeCleanup = new(2204, "SubagentDefinitionReplacedBeforeCleanup");

    private readonly IReadOnlyList<string> _writtenFiles;
    private readonly IReadOnlyList<string> _createdDirectories;
    private readonly ILogger? _logger;
    private int _disposed;

    internal SubagentMaterialization(
        string cliType,
        string directory,
        IReadOnlyList<SubagentAvailability> available,
        IReadOnlyList<string> writtenFiles,
        IReadOnlyList<string> createdDirectories,
        ILogger? logger)
    {
        CliType = cliType;
        Directory = directory;
        Available = available;
        _writtenFiles = writtenFiles;
        _createdDirectories = createdDirectories;
        _logger = logger;
    }

    /// <summary>The CLI these definitions were rendered for (one of <see cref="CliTypes"/>).</summary>
    public string CliType { get; }

    /// <summary>Absolute path of the agents directory in the run's workspace.</summary>
    public string Directory { get; }

    /// <summary>Every agent the run can delegate to, runner-written and repo-provided alike.</summary>
    public IReadOnlyList<SubagentAvailability> Available { get; }

    /// <summary>Files this run created; the only files <see cref="Dispose"/> removes.</summary>
    public IReadOnlyList<string> WrittenFiles => _writtenFiles;

    /// <summary>Context sources describing the materialized set (read-only observability).</summary>
    public IReadOnlyList<CliContextSource> Sources => Available
        .Select(a => new CliContextSource
        {
            Kind = CliContextSourceKinds.InstructionFile,
            Label = $"Subagent {a.Name}",
            Path = a.Path,
            Exists = true,
            Detail = a.Source == SubagentSources.Runner
                ? $"materialized by the runner for this run{(a.Model is null ? "" : $" (model {a.Model})")}"
                : "provided by the repository",
        })
        .ToList();

    /// <summary>
    /// Remove the definition files this run created, and any directory it had to
    /// create for them if it is now empty. Idempotent; failures are logged, never
    /// thrown — a leftover definition file is a hygiene issue, not a run failure.
    /// <para>
    /// A file whose content is no longer the definition the runner wrote is left in
    /// place: something replaced it during the run, and deleting somebody else's file
    /// is the one outcome worth a read to rule out.
    /// </para>
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var file in _writtenFiles)
        {
            try
            {
                if (!File.Exists(file)) continue;
                if (!File.ReadAllText(file).Contains(SubagentMaterializer.GeneratedMarker, StringComparison.Ordinal))
                {
                    _logger?.LogWarning(
                        ReplacedBeforeCleanup,
                        "Leaving subagent definition {Path} in place: its content is no longer the definition this run wrote",
                        file);
                    continue;
                }
                File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to remove materialized subagent definition {Path}", file);
            }
        }

        SubagentMaterializer.RemoveCreatedDirectories(_createdDirectories, _logger);
    }
}

/// <summary>
/// Writes a run's agent definitions into the CLI's project-scoped convention and
/// reports what the run can delegate to.
/// <para>
/// One rule makes this safe to run against somebody's checkout: <b>ownership is
/// established by creating a file, never by reading one.</b> A definition is written
/// with <see cref="FileMode.CreateNew"/>, so anything already at that path — a
/// committed definition, or one an earlier run left behind — makes the create fail
/// and the runner leaves the file alone, records it in the inventory, and never
/// deletes it. Content is never consulted to decide ownership, so a project may
/// commit a definition that contains <see cref="GeneratedMarker"/> without risking
/// it. A project opts out of the runner's set entirely by committing an
/// <see cref="OptOutFileName"/> file in its agents directory.
/// </para>
/// </summary>
public static class SubagentMaterializer
{
    /// <summary>
    /// Marker written into every generated definition file. It makes a generated file
    /// recognisable in a checkout and lets cleanup notice that a file it created was
    /// replaced meanwhile. It is <em>not</em> what grants the runner permission to
    /// touch a file — only having created the file in this run does that.
    /// </summary>
    public const string GeneratedMarker = "generated by coding-agent-runner for this run";

    /// <summary>A file with this name in a project's agents directory turns the runner's default set off for that project.</summary>
    public const string OptOutFileName = ".no-runner-agents";

    private static readonly EventId Materialized = new(2200, "SubagentsMaterialized");
    private static readonly EventId OptedOut = new(2201, "SubagentMaterializationOptedOut");
    private static readonly EventId WriteFailed = new(2202, "SubagentDefinitionWriteFailed");
    private static readonly EventId LeftoverAdopted = new(2203, "SubagentDefinitionLeftoverAdopted");

    /// <summary>
    /// Materialize <paramref name="options"/>' agent set into
    /// <paramref name="workingDirectory"/> for <paramref name="cliType"/>. Returns null
    /// when delegation is off, the project opted out, or nothing is available — the
    /// caller then injects no context block, because advertising an agent that does
    /// not exist is worse than saying nothing.
    /// </summary>
    public static SubagentMaterialization? Prepare(
        string cliType,
        SubagentSpec spec,
        string workingDirectory,
        DelegationOptions options,
        ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Enabled) return null;
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory)) return null;

        var timer = Stopwatch.StartNew();
        var directory = Path.GetFullPath(Path.Combine(workingDirectory, spec.DirectoryRelativePath));

        if (File.Exists(Path.Combine(directory, OptOutFileName)))
        {
            logger?.LogInformation(
                OptedOut,
                "{Cli} subagent defaults are off for {Directory}: the project committed a {OptOutFile} file",
                cliType, directory, OptOutFileName);
            return null;
        }

        var available = new List<SubagentAvailability>();
        var written = new List<string>();
        var createdDirectories = new List<string>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Whatever is already in the agents directory claims its name — the project's
        // own definitions, and equally a file some earlier run left behind. The runner
        // adds only names nothing has claimed, so it can never overwrite a file it did
        // not create.
        foreach (var (name, path, text) in ExistingDefinitions(directory, spec))
        {
            if (!claimed.Add(name)) continue;
            if (text.Contains(GeneratedMarker, StringComparison.Ordinal))
                logger?.LogWarning(
                    LeftoverAdopted,
                    "{Cli} subagent definition {Path} carries the runner's generated marker but was not created by this run — using it as it is, and leaving it in place. A run killed before its cleanup leaves these behind; remove it by hand if it does not belong in the checkout.",
                    cliType, path);
            available.Add(new SubagentAvailability(name, spec.ReadDescription(text), null, SubagentSources.Repo, path));
        }

        foreach (var agent in options.Agents ?? [])
        {
            if (agent is null || !SubagentDefinition.IsValidName(agent.Name))
            {
                logger?.LogWarning(
                    WriteFailed,
                    "Skipping a subagent definition with an unusable name '{Name}' for {Cli}",
                    agent?.Name, cliType);
                continue;
            }
            if (claimed.Contains(agent.Name)) continue;   // something already holds this name

            var file = Path.Combine(directory, agent.Name + spec.FileExtension);
            try
            {
                EnsureDirectory(directory, workingDirectory, createdDirectories);
                // CreateNew, not WriteAllText: creating the file is what makes it this
                // run's to delete later. A file that appeared since the scan above wins
                // the race and is adopted, not overwritten.
                using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var writer = new StreamWriter(stream);
                writer.Write(spec.Render(agent));
            }
            catch (Exception ex) when (ex is IOException && File.Exists(file))
            {
                logger?.LogInformation(
                    LeftoverAdopted,
                    "{Cli} subagent definition {Path} already existed — leaving it as it is",
                    cliType, file);
                claimed.Add(agent.Name);
                var existing = TryReadDescription(file, spec);
                available.Add(new SubagentAvailability(agent.Name, existing, null, SubagentSources.Repo, file));
                continue;
            }
            catch (Exception ex)
            {
                // A workspace the runner cannot write to is not a run failure: the run
                // proceeds without this agent, and the context block only advertises
                // what actually landed.
                logger?.LogWarning(WriteFailed, ex, "Could not write subagent definition {Path} for {Cli}", file, cliType);
                continue;
            }

            written.Add(file);
            claimed.Add(agent.Name);
            available.Add(new SubagentAvailability(
                agent.Name, agent.Description, agent.ModelFor(cliType), SubagentSources.Runner, file));
        }

        timer.Stop();
        if (available.Count == 0)
        {
            // Nothing landed, so nothing will be disposed — take back any directory
            // that was created for a write that then failed.
            RemoveCreatedDirectories(createdDirectories, logger);
            return null;
        }

        logger?.LogInformation(
            Materialized,
            "{Cli} subagents for this run: {WrittenCount} written by the runner, {RepoCount} provided by the repository, in {Directory} ({ElapsedMilliseconds} ms)",
            cliType,
            written.Count,
            available.Count - written.Count,
            directory,
            timer.ElapsedMilliseconds);

        return new SubagentMaterialization(cliType, directory, available, written, createdDirectories, logger);
    }

    /// <summary>
    /// Read the existing definition files in the agents directory. Best effort: an
    /// unreadable file is simply not part of the inventory.
    /// </summary>
    private static IEnumerable<(string Name, string Path, string Text)> ExistingDefinitions(string directory, SubagentSpec spec)
    {
        string[] files;
        try
        {
            if (!Directory.Exists(directory)) yield break;
            files = Directory.GetFiles(directory, "*" + spec.FileExtension, SearchOption.TopDirectoryOnly);
        }
        catch { yield break; }

        foreach (var path in files)
        {
            string text;
            try { text = File.ReadAllText(path); }
            catch { continue; }
            yield return (Path.GetFileNameWithoutExtension(path), path, text);
        }
    }

    /// <summary>Best-effort description of a definition already on disk, for the inventory.</summary>
    private static string? TryReadDescription(string path, SubagentSpec spec)
    {
        try { return spec.ReadDescription(File.ReadAllText(path)); }
        catch { return null; }
    }

    /// <summary>
    /// Remove directories the runner created, deepest first, and only while they are
    /// still empty. Shared by <see cref="SubagentMaterialization.Dispose"/> and the
    /// nothing-landed path in <see cref="Prepare"/>.
    /// </summary>
    internal static void RemoveCreatedDirectories(IReadOnlyList<string> createdDirectories, ILogger? logger)
    {
        foreach (var dir in createdDirectories.Reverse())
        {
            try
            {
                if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any())
                    Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to remove empty subagent directory {Path}", dir);
            }
        }
    }

    /// <summary>
    /// Create the agents directory, remembering every level the runner had to create
    /// so disposal can take exactly those back out again. Levels that already existed
    /// (a repo's own <c>.claude</c>, say) are never recorded and never removed.
    /// </summary>
    private static void EnsureDirectory(string directory, string workingDirectory, List<string> created)
    {
        if (Directory.Exists(directory)) return;

        var root = Path.GetFullPath(workingDirectory);
        var missing = new List<string>();
        for (var dir = directory;
             !string.IsNullOrEmpty(dir) && !Directory.Exists(dir) && dir.Length > root.Length;
             dir = Path.GetDirectoryName(dir) ?? "")
        {
            missing.Add(dir);
        }

        Directory.CreateDirectory(directory);
        missing.Reverse();
        created.AddRange(missing);
    }
}

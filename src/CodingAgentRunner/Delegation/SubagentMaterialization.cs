using System.Diagnostics;
using Microsoft.Extensions.Logging;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Delegation;

/// <summary>Where one agent available to a run came from.</summary>
public static class SubagentSources
{
    /// <summary>The runner wrote this definition for the run and removes it when the run ends.</summary>
    public const string Runner = "runner";

    /// <summary>The definition is committed in the repository. The runner never writes, overwrites, or deletes it.</summary>
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
/// One run's materialized agent set: the definition files the runner wrote into the
/// workspace plus the repo-provided ones it found. Owned by the run — disposing it
/// deletes <em>only</em> the files the runner itself created, so a project's own
/// agent definitions survive untouched.
/// </summary>
public sealed class SubagentMaterialization : IDisposable
{
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

    /// <summary>Files the runner created for this run; the only files <see cref="Dispose"/> removes.</summary>
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
    /// Remove the definition files the runner wrote, and any directory it had to
    /// create for them if it is now empty. Idempotent; failures are logged, never
    /// thrown — a leftover definition file is a hygiene issue, not a run failure, and
    /// the next run rewrites it in place.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var file in _writtenFiles)
        {
            try
            {
                if (File.Exists(file)) File.Delete(file);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to remove materialized subagent definition {Path}", file);
            }
        }

        // Reverse order: the deepest directory the runner created is emptied first.
        foreach (var dir in _createdDirectories.Reverse())
        {
            try
            {
                if (System.IO.Directory.Exists(dir) && !System.IO.Directory.EnumerateFileSystemEntries(dir).Any())
                    System.IO.Directory.Delete(dir);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "Failed to remove empty subagent directory {Path}", dir);
            }
        }
    }
}

/// <summary>
/// Writes a run's agent definitions into the CLI's project-scoped convention and
/// reports what the run can delegate to.
/// <para>
/// Two rules make this safe to run against somebody's checkout. A generated file
/// carries <see cref="GeneratedMarker"/>, and only a file carrying that marker is
/// ever overwritten or deleted — a repo's own definition of the same name wins and
/// is left alone. A project opts out of the runner's set entirely by committing an
/// <see cref="OptOutFileName"/> file in its agents directory.
/// </para>
/// </summary>
public static class SubagentMaterializer
{
    /// <summary>Marker written into every generated definition file; its presence is what makes the file the runner's to overwrite and delete.</summary>
    public const string GeneratedMarker = "generated by coding-agent-runner for this run";

    /// <summary>A file with this name in a project's agents directory turns the runner's default set off for that project.</summary>
    public const string OptOutFileName = ".no-runner-agents";

    private static readonly EventId Materialized = new(2200, "SubagentsMaterialized");
    private static readonly EventId OptedOut = new(2201, "SubagentMaterializationOptedOut");
    private static readonly EventId WriteFailed = new(2202, "SubagentDefinitionWriteFailed");

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

        // Repo-provided definitions first: they claim their names, so the runner's
        // default of the same name is skipped rather than overwritten.
        foreach (var (name, path, text) in ExistingDefinitions(directory, spec))
        {
            if (text.Contains(GeneratedMarker, StringComparison.Ordinal)) continue;   // a runner file, not the repo's
            claimed.Add(name);
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
            if (claimed.Contains(agent.Name)) continue;   // the repository defines this one

            var file = Path.Combine(directory, agent.Name + spec.FileExtension);
            try
            {
                EnsureDirectory(directory, workingDirectory, createdDirectories);
                File.WriteAllText(file, spec.Render(agent));
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
        if (available.Count == 0) return null;

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

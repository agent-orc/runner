namespace CodingAgentRunner.Abstractions;

/// <summary>
/// Tells the runner where to persist per-run output logs and the active-jobs
/// bookkeeping file, so the library imposes no directory convention of its own.
/// </summary>
public interface IRunLogPathProvider
{
    /// <summary>Directory for a run's durable per-stream output log.</summary>
    string GetRunLogDirectory(string runId);

    /// <summary>File that tracks currently-active runs (for orphan reaping across restarts).</summary>
    string GetActiveJobsFile();
}

/// <summary>
/// Default provider that places per-run logs and the active-runs file under a
/// <c>coding-agent-runner</c> folder in the temp root from an
/// <see cref="IUserHomeProvider"/>.
/// </summary>
internal sealed class DefaultRunLogPathProvider : IRunLogPathProvider
{
    private readonly string _root;

    /// <summary>Create a provider rooted under the given home provider's temp root.</summary>
    public DefaultRunLogPathProvider(IUserHomeProvider? home = null)
    {
        var tempRoot = (home ?? new DefaultUserHomeProvider()).GetTempRoot();
        _root = Path.Combine(tempRoot, "coding-agent-runner");
    }

    /// <inheritdoc />
    public string GetRunLogDirectory(string runId)
        => Path.Combine(_root, "cli-output", Sanitize(runId));

    /// <inheritdoc />
    public string GetActiveJobsFile() => Path.Combine(_root, "active-runs.json");

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "run";
        var s = value.Trim();
        foreach (var c in Path.GetInvalidFileNameChars())
            s = s.Replace(c, '_');
        return s.Length == 0 ? "run" : s;
    }
}

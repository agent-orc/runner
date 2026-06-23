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

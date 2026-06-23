namespace CodingAgentRunner.Model;

/// <summary>
/// Turns a process exit code plus the runner's stop reason into a terminal
/// <see cref="RunStatus"/>.
///
/// <para>The load-bearing distinction: a runner-initiated stop <em>always</em>
/// carries a <see cref="RunStopReason"/>. So a non-zero exit with <em>no</em>
/// reason is a self-crash (<see cref="RunStatus.Failed"/>), not a kill — without
/// this, every <c>Process.Kill</c> (which hands back <c>-1</c> on Windows) would
/// be misread as a failure.</para>
/// </summary>
public static class RunStatusClassifier
{
    /// <summary>Classify a finished run. A null <paramref name="exitCode"/> is treated as non-clean.</summary>
    public static RunStatus Classify(int? exitCode, RunStopReason stopReason)
    {
        if (stopReason != RunStopReason.None) return RunStatus.Stopped;
        return exitCode == 0 ? RunStatus.Completed : RunStatus.Failed;
    }
}

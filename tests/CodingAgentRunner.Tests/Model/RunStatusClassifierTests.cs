using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Model;

public class RunStatusClassifierTests
{
    [Theory]
    // No stop reason → the exit code decides.
    [InlineData(0, RunStopReason.None, RunStatus.Completed)]
    [InlineData(1, RunStopReason.None, RunStatus.Failed)]
    [InlineData(-1, RunStopReason.None, RunStatus.Failed)]   // Process.Kill / TerminateProcess self-crash signature
    [InlineData(137, RunStopReason.None, RunStatus.Failed)]
    [InlineData(null, RunStopReason.None, RunStatus.Failed)]
    // A stop reason → always 'stopped', regardless of exit code.
    [InlineData(0, RunStopReason.UserStop, RunStatus.Stopped)]
    [InlineData(-1, RunStopReason.Watchdog, RunStatus.Stopped)]
    [InlineData(1, RunStopReason.Cancelled, RunStatus.Stopped)]
    [InlineData(0, RunStopReason.QuotaCapExceeded, RunStatus.Stopped)]
    public void Classify_DistinguishesStoppedFromCompletedAndFailed(
        int? exitCode, RunStopReason reason, RunStatus expected)
    {
        Assert.Equal(expected, RunStatusClassifier.Classify(exitCode, reason));
    }
}

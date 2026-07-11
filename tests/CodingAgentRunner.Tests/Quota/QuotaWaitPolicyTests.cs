using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Quota;

namespace CodingAgentRunner.Tests.Quota;

public class QuotaWaitPolicyTests
{
    private static readonly DateTime Now = new(2026, 7, 11, 12, 0, 0, DateTimeKind.Utc);

    private static WaitOnQuotaOptions Enabled(TimeSpan? threshold = null) => new()
    {
        Enabled = true,
        Threshold = threshold ?? TimeSpan.FromMinutes(30),
        QuotaService = new QuotaService([]),
    };

    [Fact]
    public void SelectReset_UsesNearestFutureResetInsideThreshold()
    {
        var snapshot = new QuotaSnapshot
        {
            Windows =
            [
                new QuotaWindow { Label = "weekly", ResetAt = Now.AddHours(4) },
                new QuotaWindow { Label = "5-hour", ResetAt = Now.AddMinutes(18) },
                new QuotaWindow { Label = "stale", ResetAt = Now.AddMinutes(-1) },
            ],
        };

        Assert.Equal(Now.AddMinutes(18), QuotaWaitPolicy.SelectReset(snapshot, Enabled(), Now));
    }

    [Fact]
    public void SelectReset_FailsOpenForDisabledUnknownImplausibleOrDistantQuota()
    {
        var near = new QuotaSnapshot { Windows = [new QuotaWindow { ResetAt = Now.AddMinutes(5) }] };
        var errored = near with { Error = "probe unavailable" };
        var distant = near with { Windows = [new QuotaWindow { ResetAt = Now.AddMinutes(31) }] };

        Assert.Null(QuotaWaitPolicy.SelectReset(near, Enabled() with { Enabled = false }, Now));
        Assert.Null(QuotaWaitPolicy.SelectReset(null, Enabled(), Now));
        Assert.Null(QuotaWaitPolicy.SelectReset(errored, Enabled(), Now));
        Assert.Null(QuotaWaitPolicy.SelectReset(distant, Enabled(), Now));
    }

    [Theory]
    [InlineData("rate limit exceeded")]
    [InlineData("Usage limit reached; try later")]
    [InlineData("quota exhausted")]
    public void IsQuotaLimitFailure_RecognizesConservativeFailurePhrases(string reason)
        => Assert.True(QuotaWaitPolicy.IsQuotaLimitFailure(reason));

    [Fact]
    public void IsQuotaLimitFailure_DoesNotTreatGenericFailuresAsQuota()
        => Assert.False(QuotaWaitPolicy.IsQuotaLimitFailure("authentication failed"));
}

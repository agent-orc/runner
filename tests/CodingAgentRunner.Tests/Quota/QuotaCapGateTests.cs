using CodingAgentRunner.Events;
using CodingAgentRunner.Model;
using CodingAgentRunner.Quota;
using Xunit;

namespace CodingAgentRunner.Tests.Quota;

public class QuotaCapGateTests
{
    // A probe that returns a fixed snapshot, so we can seed the cache deterministically.
    private static QuotaService WithSnapshot(string cli, double usedPct, DateTime? reset = null)
    {
        var probe = new DelegateQuotaProbe(cli, _ => Task.FromResult(new QuotaSnapshot
        {
            CliType = cli,
            Windows = [new QuotaWindow { Label = "5-hour", UsedPct = usedPct, ResetAt = reset }],
        }));
        var svc = new QuotaService([probe]);
        svc.RefreshAllAsync().GetAwaiter().GetResult();   // populate the cache
        return svc;
    }

    [Fact]
    public void NoCap_Configured_IsAlwaysOpen()
    {
        var svc = WithSnapshot("claude", 99);
        Assert.True(svc.Gate("claude").Allowed);   // 99% used but no cap set
    }

    [Fact]
    public void UnderCap_IsOpen_AtOrOverCap_IsBlocked_WithReset()
    {
        var reset = new DateTime(2030, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var svc = WithSnapshot("claude", 96, reset);
        svc.Cap("claude", stopAtPercent: 95);

        var gate = svc.Gate("claude");
        Assert.False(gate.Allowed);
        Assert.Contains("96%", gate.Reason);
        Assert.Equal(reset, gate.RetryAfter);
        Assert.True(svc.IsAtCap("claude"));
    }

    [Fact]
    public void Cap_IsCaseInsensitive_AndPerCli()
    {
        var svc = WithSnapshot("claude", 96);
        svc.Cap("CLAUDE", 95);
        Assert.False(svc.Gate("claude").Allowed);   // normalized
        Assert.True(svc.Gate("codex").Allowed);     // unrelated CLI, no cap, no data
    }

    [Fact]
    public void NoData_FailsOpen_EvenWithACap()
    {
        var svc = new QuotaService(System.Array.Empty<IQuotaProbe>());
        svc.Cap("claude", 90);
        Assert.True(svc.Gate("claude").Allowed);    // capped but never probed → don't block
    }

    [Fact]
    public void Observe_Overage_MarksAtCap_FromAFreeEvent()
    {
        var svc = new QuotaService(System.Array.Empty<IQuotaProbe>());
        svc.Cap("claude", 100);

        long resetUnix = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var harvested = svc.Observe("claude", new CliRunEvent.RateLimitObserved(
            Window: "5-hour", Status: "rejected", ResetsAt: resetUnix,
            OverageStatus: "over", IsUsingOverage: true));

        Assert.True(harvested);
        var snap = svc.GetCachedFor("claude");
        Assert.NotNull(snap);
        Assert.Equal("event", snap!.Source);
        Assert.Equal(100, snap.MaxUsedPct);
        Assert.False(svc.Gate("claude").Allowed);   // overage → at the 100% cap, no probe needed
    }

    [Fact]
    public void Observe_IgnoresNonRateLimitEvents()
    {
        var svc = new QuotaService(System.Array.Empty<IQuotaProbe>());
        Assert.False(svc.Observe("claude", new CliRunEvent.OutputDelta("hi")));
        Assert.Null(svc.GetCachedFor("claude"));
    }

    [Fact]
    public void Observe_DoesNotLower_AKnownUsage()
    {
        var svc = WithSnapshot("claude", 95);   // a probe established 95%
        // A non-overage event arrives (carries no percent): must NOT drop usage to 0.
        svc.Observe("claude", new CliRunEvent.RateLimitObserved(
            Window: "5-hour", Status: "allowed", ResetsAt: 0, OverageStatus: null, IsUsingOverage: false));
        Assert.Equal(95, svc.GetCachedFor("claude")!.MaxUsedPct);
    }

    [Theory]
    [InlineData(95, 95, false)]    // usage == cap → blocked (>= semantics)
    [InlineData(94, 95, true)]     // just under → allowed
    [InlineData(0, 0, false)]      // cap 0 → blocked even at 0% (always-block)
    [InlineData(99, 100, true)]    // cap 100 → allowed at 99
    [InlineData(100, 100, false)]  // cap 100 → blocked at exactly 100
    public void Gate_CapBoundaries(double usedPct, double cap, bool allowed)
    {
        var svc = WithSnapshot("claude", usedPct);
        svc.Cap("claude", cap);
        Assert.Equal(allowed, svc.Gate("claude").Allowed);
    }

    [Fact]
    public void Cap_Negative_Throws()
        => Assert.Throws<System.ArgumentOutOfRangeException>(
            () => new QuotaService(System.Array.Empty<IQuotaProbe>()).Cap("claude", -1));

    [Fact]
    public void Observe_PreservesOtherWindows_OnLabelMerge()
    {
        var probe = new DelegateQuotaProbe("claude", _ => Task.FromResult(new QuotaSnapshot
        {
            CliType = "claude",
            Windows =
            [
                new QuotaWindow { Label = "5-hour", UsedPct = 40 },
                new QuotaWindow { Label = "weekly", UsedPct = 70 },
            ],
        }));
        var svc = new QuotaService([probe]);
        svc.RefreshAllAsync().GetAwaiter().GetResult();

        svc.Observe("claude", new CliRunEvent.RateLimitObserved(
            Window: "5-hour", Status: "warning", ResetsAt: 0, OverageStatus: null, IsUsingOverage: true));

        var snap = svc.GetCachedFor("claude")!;
        Assert.Equal(2, snap.Windows.Count);                                            // weekly preserved
        Assert.Equal(100, snap.Windows.Single(w => w.Label == "5-hour").UsedPct);       // overage marked
        Assert.Equal(70, snap.Windows.Single(w => w.Label == "weekly").UsedPct);        // untouched
    }

    [Fact]
    public void Observe_Overage_DoesNotDecayBelow100_OnLaterNonOverage()
    {
        var svc = new QuotaService(System.Array.Empty<IQuotaProbe>());
        svc.Observe("claude", new CliRunEvent.RateLimitObserved("5-hour", "rejected", 0, "over", IsUsingOverage: true));
        Assert.Equal(100, svc.GetCachedFor("claude")!.MaxUsedPct);
        svc.Observe("claude", new CliRunEvent.RateLimitObserved("5-hour", "allowed", 0, null, IsUsingOverage: false));
        Assert.Equal(100, svc.GetCachedFor("claude")!.MaxUsedPct);   // stays until a real probe corrects it
    }

    [Fact]
    public void MaxUsedPct_IsZero_WhenNoWindows()
        => Assert.Equal(0, new QuotaSnapshot { CliType = "claude" }.MaxUsedPct);

    [Theory]
    [InlineData(50, 600)]    // < 90% → default 10 min
    [InlineData(90, 120)]    // == 90% tier → 2 min
    [InlineData(96, 120)]    // 90..97 → 2 min
    [InlineData(97, 30)]     // == 97% tier → 30 s
    [InlineData(100, 30)]    // > 97% → 30 s
    public void EffectiveTtl_EscalatesAtTierBoundaries(double usedPct, int expectedSeconds)
    {
        var opts = new QuotaCacheOptions();   // default tiers: 90→2min, 97→30s; default 10min
        var snap = new QuotaSnapshot { CliType = "claude", Windows = [new QuotaWindow { Label = "w", UsedPct = usedPct }] };
        Assert.Equal(expectedSeconds, (int)opts.EffectiveTtl(snap).TotalSeconds);
    }

    [Fact]
    public void FileQuotaCacheStore_Roundtrips_CreatesDir_AndToleratesCorruption()
    {
        var dir = Path.Combine(Path.GetTempPath(), "car-quota-" + System.Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "quota.json");   // dir does not exist yet
        try
        {
            var store = new FileQuotaCacheStore(path);
            Assert.Empty(store.Read());               // missing file → empty, no throw

            store.Write([new QuotaSnapshot { CliType = "claude", Plan = "Pro", Windows = [new QuotaWindow { Label = "5-hour", UsedPct = 42 }] }]);
            var back = store.Read();
            Assert.Single(back);
            Assert.Equal("claude", back[0].CliType);
            Assert.Equal(42, back[0].Windows.Single().UsedPct);

            File.WriteAllText(path, "{ not valid json ]");   // corrupt
            Assert.Empty(store.Read());               // tolerated → empty, no throw
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

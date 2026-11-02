using CodingAgentRunner.Model;
using CodingAgentRunner.Quota;
using Xunit;

namespace CodingAgentRunner.Tests.Quota;

public class GlobalQuotaCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "car-global-" + Guid.NewGuid().ToString("N"));

    public GlobalQuotaCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string CachePath => Path.Combine(_dir, "quota-cache.json");

    private static QuotaSnapshot Snapshot(string cli, double usedPct, DateTime fetchedAt) => new()
    {
        CliType = cli,
        FetchedAt = fetchedAt,
        Windows = [new QuotaWindow { Label = "5-hour", UsedPct = usedPct }],
        Source = "test",
    };

    // ── Global path resolution ──────────────────────────────────────────

    private static string? NoEnv(string _) => null;

    [Fact]
    public void Env_override_wins_over_the_os_native_location()
    {
        var path = FileQuotaCacheStore.ResolveGlobalPath(
            name => name == "CODING_AGENT_RUNNER_CACHE_DIR" ? @"D:\mounted-cache" : null,
            localAppDataDir: () => @"C:\Users\x\AppData\Local",
            userHome: () => @"C:\Users\x");

        Assert.Equal(Path.Combine(@"D:\mounted-cache", "quota-cache.json"), path);
    }

    [Fact]
    public void Default_is_the_os_native_per_user_app_data_dir()
    {
        var path = FileQuotaCacheStore.ResolveGlobalPath(
            NoEnv,
            localAppDataDir: () => @"C:\Users\x\AppData\Local",
            userHome: () => @"C:\Users\x");

        Assert.Equal(Path.Combine(@"C:\Users\x\AppData\Local", "coding-agent-runner", "quota-cache.json"), path);
    }

    [Fact]
    public void Missing_app_data_dir_falls_back_to_a_dotdir_in_the_home()
    {
        var path = FileQuotaCacheStore.ResolveGlobalPath(
            NoEnv,
            localAppDataDir: () => "",   // platforms without a known app-data folder report empty
            userHome: () => @"C:\Users\x");

        Assert.Equal(Path.Combine(@"C:\Users\x", ".coding-agent-runner", "quota-cache.json"), path);
    }

    [Fact]
    public void GlobalPath_resolves_to_a_writable_rooted_location()
    {
        var path = FileQuotaCacheStore.GlobalPath();

        Assert.True(Path.IsPathRooted(path));
        Assert.EndsWith("quota-cache.json", path);
    }

    // ── Merge-on-write ──────────────────────────────────────────────────

    [Fact]
    public void Writers_for_different_clis_do_not_erase_each_other()
    {
        // Two stores on the SAME path, as two processes would have.
        var a = new FileQuotaCacheStore(CachePath);
        var b = new FileQuotaCacheStore(CachePath);

        a.Write([Snapshot("claude", 10, DateTime.UtcNow)]);
        b.Write([Snapshot("codex", 20, DateTime.UtcNow)]);

        var merged = a.Read();
        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, s => s.CliType == "claude");
        Assert.Contains(merged, s => s.CliType == "codex");
    }

    [Fact]
    public void Freshest_snapshot_per_cli_wins_the_merge()
    {
        var store = new FileQuotaCacheStore(CachePath);
        var now = DateTime.UtcNow;

        store.Write([Snapshot("claude", 50, now)]);
        store.Write([Snapshot("claude", 10, now.AddMinutes(-5))]);   // older — must NOT win

        var claude = Assert.Single(store.Read());
        Assert.Equal(50, claude.MaxUsedPct);

        store.Write([Snapshot("claude", 60, now.AddMinutes(1))]);    // newer — must win
        Assert.Equal(60, Assert.Single(store.Read()).MaxUsedPct);
    }

    [Fact]
    public void Merge_is_case_insensitive_and_drops_nameless_snapshots()
    {
        var merged = FileQuotaCacheStore.Merge(
            [Snapshot("Claude", 10, DateTime.UtcNow.AddMinutes(-1)), new QuotaSnapshot { CliType = "" }],
            [Snapshot("claude", 20, DateTime.UtcNow)]);

        var only = Assert.Single(merged);
        Assert.Equal(20, only.MaxUsedPct);
    }

    // ── QuotaService adoption ───────────────────────────────────────────

    [Fact]
    public void Stale_service_adopts_a_fresh_snapshot_another_process_stored_instead_of_probing()
    {
        var store = new FileQuotaCacheStore(CachePath);
        var probeCalls = 0;
        var probe = new DelegateQuotaProbe(CliTypes.Claude, _ =>
        {
            Interlocked.Increment(ref probeCalls);
            return Task.FromResult(Snapshot("claude", 99, DateTime.UtcNow));
        });

        // Constructed against an EMPTY store → nothing hydrated, claude is stale.
        var quota = new QuotaService([probe], store: store);

        // Another process refreshes the shared file in the meantime.
        new FileQuotaCacheStore(CachePath).Write([Snapshot("claude", 42, DateTime.UtcNow)]);

        var report = quota.GetWithBackgroundRefresh();

        Assert.Equal(0, probeCalls);   // adopted, not probed
        Assert.Equal(42, report.Snapshots.Single().MaxUsedPct);
        Assert.False(quota.IsStale(CliTypes.Claude));
    }

    [Fact]
    public async Task A_stale_stored_snapshot_does_not_prevent_the_probe()
    {
        var store = new FileQuotaCacheStore(CachePath);
        store.Write([Snapshot("claude", 42, DateTime.UtcNow.AddHours(-2))]);   // long past the 10-min TTL

        var probed = new TaskCompletionSource();
        var probe = new DelegateQuotaProbe(CliTypes.Claude, _ =>
        {
            probed.TrySetResult();
            return Task.FromResult(Snapshot("claude", 7, DateTime.UtcNow));
        });

        var quota = new QuotaService([probe], store: store);
        quota.GetWithBackgroundRefresh();

        // Throws TimeoutException when the probe never runs for the stale stored snapshot.
        await probed.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }
}

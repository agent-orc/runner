using System.Diagnostics;
using CodingAgentRunner.Abstractions;
using CodingAgentRunner.Events;
using CodingAgentRunner.Execution;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Tests.Execution;

public class PreSpawnHealthTests
{
    private static CliRunEngine Driver(PreSpawnHealth? health, ICliProcessSpawner? spawner = null) => new(
        new CliDescriptor
        {
            CliType = CliTypes.Claude,
            GetCliPath = _ => "unused",
            BuildLaunch = ctx => new LaunchSpec { Executable = "unused", WorkingDirectory = ctx.Request.WorkingDirectory },
            Parse = static (_, _, _) => Array.Empty<CliRunEvent>(),
            InterruptClassifier = InterruptClassifiers.None,
            Liveness = LivenessSpec.InBandDefault,
            Capabilities = static model => new CliCapabilities { CliType = CliTypes.Claude, Model = model },
            EnsureHealthy = health,
        },
        new CliOptions { AllowAgentGitMutation = true, Spawner = spawner });

    [Fact]
    public async Task EnsureHealthyAsync_Healthy_DoesNotSpawnAndReturnsTypedResult()
    {
        var calls = 0;
        var spawner = new CountingSpawner();
        ICliDriver driver = Driver((_, _) =>
        {
            calls++;
            return Task.FromResult(PreSpawnHealthResult.Healthy());
        }, spawner);

        var result = await driver.EnsureHealthyAsync();

        Assert.Equal(1, calls);
        Assert.Equal(PreSpawnHealthStatus.Healthy, result.Status);
        Assert.True(result.IsHealthy);
        Assert.Equal(0, spawner.Count);
    }

    [Fact]
    public async Task EnsureHealthyAsync_RepairSucceeded_ReturnsActions()
    {
        ICliDriver driver = Driver((_, _) => Task.FromResult(PreSpawnHealthResult.Repaired(["restored claude.cmd"])));

        var result = await driver.EnsureHealthyAsync();

        Assert.Equal(PreSpawnHealthStatus.Repaired, result.Status);
        Assert.True(result.IsHealthy);
        Assert.Equal(["restored claude.cmd"], result.Actions);
        Assert.Null(result.Error);
    }

    [Fact]
    public async Task EnsureHealthyAsync_RepairFailed_ReturnsActionableTypedError()
    {
        ICliDriver driver = Driver((_, _) => Task.FromResult(
            PreSpawnHealthResult.Failed("npm global bin not found", ["restored orphan shim"])));

        var result = await driver.EnsureHealthyAsync();

        Assert.Equal(PreSpawnHealthStatus.Failed, result.Status);
        Assert.False(result.IsHealthy);
        Assert.Equal("npm global bin not found", result.Error);
        Assert.Equal(["restored orphan shim"], result.Actions);
    }

    [Fact]
    public async Task EnsureHealthyAsync_Cancellation_IsPropagated()
    {
        ICliDriver driver = Driver(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return PreSpawnHealthResult.Healthy();
        });
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => driver.EnsureHealthyAsync(cancellation.Token));
    }

    [Fact]
    public async Task StartAsync_UsesTheSameHealthOperationAndDoesNotSpawnAfterFailure()
    {
        var calls = 0;
        var spawner = new CountingSpawner();
        var driver = Driver((_, _) =>
        {
            calls++;
            return Task.FromResult(PreSpawnHealthResult.Failed("repair did not restore claude"));
        }, spawner);

        var (run, error) = await driver.StartAsync(new CliRunRequest
        {
            RunId = "health-failure", Prompt = "unused", WorkingDirectory = Path.GetTempPath(),
        });

        Assert.Null(run);
        Assert.Equal("claude CLI not available: repair did not restore claude", error);
        Assert.Equal(1, calls);
        Assert.Equal(0, spawner.Count);
    }

    private sealed class CountingSpawner : ICliProcessSpawner
    {
        public int Count { get; private set; }

        public CliSpawn Spawn(ProcessStartInfo startInfo)
        {
            Count++;
            throw new InvalidOperationException("Health checks must not spawn.");
        }
    }
}

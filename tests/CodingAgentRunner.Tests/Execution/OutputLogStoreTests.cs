using CodingAgentRunner.Execution.Logging;
using CodingAgentRunner.Model;
using Xunit;

namespace CodingAgentRunner.Tests.Execution;

public class OutputLogStoreTests
{
    private static string TempFile() =>
        Path.Combine(Path.GetTempPath(), "car-log-" + Guid.NewGuid().ToString("N"), "stdout.jsonl");

    private static CliOutputLine Line(string text, string stream = "stdout", int msOffset = 0) => new()
    {
        Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(msOffset),
        Stream = stream,
        Text = text,
    };

    [Fact]
    public void Append_ThenReadAll_RoundTrips()
    {
        var path = TempFile();
        try
        {
            using (var store = new CliOutputLogStore(path))
            {
                Assert.True(store.Append(Line("first")));
                Assert.True(store.Append(Line("second")));
                Assert.Equal(0, store.TotalFailures);
            }
            var lines = CliOutputLogStore.ReadAll(path);
            Assert.Equal(["first", "second"], lines.Select(l => l.Text));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void Reset_TruncatesPriorContent()
    {
        var path = TempFile();
        try
        {
            using var store = new CliOutputLogStore(path);
            store.Append(Line("stale"));
            store.Reset();
            store.Append(Line("fresh"));
            Assert.Equal(["fresh"], store.ReadAll().Select(l => l.Text));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ReadAll_SkipsAPartiallyWrittenTrailingLine()
    {
        var path = TempFile();
        try
        {
            using (var store = new CliOutputLogStore(path))
                store.Append(Line("good"));
            // Simulate a crash mid-write: append a truncated JSON fragment.
            File.AppendAllText(path, "{\"Timestamp\":\"2026-01-01");
            var lines = CliOutputLogStore.ReadAll(path);
            Assert.Equal(["good"], lines.Select(l => l.Text));
        }
        finally { Cleanup(path); }
    }

    [Fact]
    public void ReadAll_OnMissingFile_ReturnsEmpty()
    {
        Assert.Empty(CliOutputLogStore.ReadAll(Path.Combine(Path.GetTempPath(), "car-nope-" + Guid.NewGuid().ToString("N") + ".jsonl")));
    }

    private static void Cleanup(string path)
    {
        try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch { /* ignore */ }
    }
}

public class RunLogStoreTests
{
    private static string TempDir() =>
        Path.Combine(Path.GetTempPath(), "car-run-" + Guid.NewGuid().ToString("N"));

    private static CliOutputLine Line(string text, string stream, int msOffset) => new()
    {
        Timestamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(msOffset),
        Stream = stream,
        Text = text,
    };

    [Fact]
    public void PerStreamFiles_AreSeparate_AndReadMergedInterleavesByTimestamp()
    {
        var dir = TempDir();
        try
        {
            using (var store = new RunLogStore(dir))
            {
                store.Append(Line("out-1", "stdout", 0));
                store.Append(Line("err-1", "stderr", 10));
                store.Append(Line("out-2", "stdout", 20));
            }
            // Distinct per-stream files exist.
            Assert.True(File.Exists(Path.Combine(dir, "stdout.jsonl")));
            Assert.True(File.Exists(Path.Combine(dir, "stderr.jsonl")));

            var merged = RunLogStore.ReadMerged(dir);
            Assert.Equal(["out-1", "err-1", "out-2"], merged.Select(l => l.Text));
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { /* ignore */ } }
    }

    [Fact]
    public void DeleteRun_RemovesTheDirectory()
    {
        var dir = TempDir();
        using (var store = new RunLogStore(dir))
            store.Append(Line("x", "stdout", 0));
        Assert.True(Directory.Exists(dir));
        RunLogStore.DeleteRun(dir);
        Assert.False(Directory.Exists(dir));
    }
}

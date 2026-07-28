using CodingAgentRunner.Delegation;
using CodingAgentRunner.Model;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CodingAgentRunner.Tests.Delegation;

/// <summary>
/// The block is the half of the feature the agent can actually see. These pin what
/// it says, that a project can replace the rule text, and that it stays out of the
/// prompt when there is nothing to advertise.
/// </summary>
public class DelegationContextBlockTests
{
    private static string NewWorkspace()
    {
        var dir = Path.Combine(Path.GetTempPath(), "car-test-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void Cleanup(string dir)
    {
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void Compose_AppendsTheRuleAndTheAgentInventory()
    {
        var workspace = NewWorkspace();
        try
        {
            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions())!;

            var composed = DelegationContextBlock.Compose(
                "Refactor the parser", prep, workspace, new DelegationOptions());

            Assert.NotNull(composed);
            Assert.StartsWith("Refactor the parser", composed);
            Assert.Contains(DelegationContextBlock.OpenTag, composed);
            Assert.EndsWith(DelegationContextBlock.CloseTag, composed);
            Assert.Contains(DelegationContextBlock.DefaultRule.Split('\n')[0], composed);
            Assert.Contains("\"name\":\"mechanical\"", composed);
            Assert.Contains("\"model\":\"haiku\"", composed);
            Assert.Contains("\"name\":\"checker\"", composed);
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Compose_PrefersAProjectSuppliedRule()
    {
        var workspace = NewWorkspace();
        try
        {
            var options = new DelegationOptions();
            var rulePath = Path.Combine(workspace, options.ContextBlockRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(rulePath)!);
            File.WriteAllText(rulePath, "Delegate greps to the mechanical agent. Nothing else.");

            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, options)!;
            var composed = DelegationContextBlock.Compose("Task", prep, workspace, options);

            Assert.NotNull(composed);
            Assert.Contains("Delegate greps to the mechanical agent. Nothing else.", composed);
            Assert.DoesNotContain(DelegationContextBlock.DefaultRule.Split('\n')[0], composed);
            Assert.Contains("\"name\":\"mechanical\"", composed);        // the inventory is appended either way
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void Compose_ReturnsNull_WhenInjectionIsOff()
    {
        var workspace = NewWorkspace();
        try
        {
            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions())!;

            Assert.Null(DelegationContextBlock.Compose(
                "Task", prep, workspace, new DelegationOptions { InjectContextBlock = false }));
        }
        finally { Cleanup(workspace); }
    }

    /// <summary>
    /// A run emits both of these, and a consumer filtering its log by EventId has to be
    /// able to tell them apart. They shared id 2203 until this test existed: the names
    /// differed, so nothing looked wrong in a rendered log, and only a filter on the
    /// numeric id would have shown the two events merged.
    /// </summary>
    [Fact]
    public void MaterializationAndInjection_LogUnderDistinctEventIds()
    {
        var workspace = NewWorkspace();
        try
        {
            var logger = new RecordingLogger();
            using var prep = SubagentMaterializer.Prepare(
                CliTypes.Claude, SubagentRenderers.Claude, workspace, new DelegationOptions(), logger)!;
            DelegationContextBlock.Compose("Task", prep, workspace, new DelegationOptions(), logger);

            var materialized = Assert.Single(logger.Events, e => e.Name == "SubagentsMaterialized");
            var injected = Assert.Single(logger.Events, e => e.Name == "DelegationContextInjected");
            Assert.NotEqual(materialized.Id, injected.Id);

            // Every id the delegation block hands out is its own.
            Assert.Equal(logger.Events.Select(e => e.Id).Distinct().Count(), logger.Events.Select(e => e.Name).Distinct().Count());
        }
        finally { Cleanup(workspace); }
    }

    [Fact]
    public void CodexIsNotAdvertisedInThePrompt()
    {
        // codex exec accepts the collab tooling but never creates a child thread, so
        // a delegated subtask comes back invented. The definitions ship; the rule does
        // not. See docs/delegation.md.
        Assert.False(SubagentRenderers.Codex.AdvertiseInPrompt);
        Assert.True(SubagentRenderers.Claude.AdvertiseInPrompt);
    }

    /// <summary>Collects the <see cref="EventId"/> of every record written, and nothing else.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<EventId> Events { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Events.Add(eventId);
    }
}

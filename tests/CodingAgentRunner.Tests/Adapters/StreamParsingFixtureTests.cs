using System.Text;
using System.Text.Json;
using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;
using CodingAgentRunner.Model;

namespace CodingAgentRunner.Tests.Adapters;

/// <summary>
/// Replays checked-in CLI streams from the 2417 incident. These fixtures are
/// deliberately opt-in because the payloads include a very long physical JSONL
/// line. Set CODING_AGENT_RUNNER_STREAM_FIXTURES=1 to run them.
/// </summary>
public class StreamParsingFixtureTests
{
    private const string OptInVariable = "CODING_AGENT_RUNNER_STREAM_FIXTURES";
    private static readonly string FixtureRoot =
        Path.Combine(AppContext.BaseDirectory, "testdata", "cli-fixtures");

    public static IEnumerable<object[]> SupportedCliFixtures()
    {
        yield return ["claude"];
        yield return ["codex"];
        yield return ["gemini"];
        yield return ["antigravity"];
    }

    [StreamFixtureTheory]
    [Trait("Category", "StreamParsingFixtures")]
    [MemberData(nameof(SupportedCliFixtures))]
    public void PayloadFrames_RoundTripWithoutFalseTerminalOrStatusSignals(string cli)
    {
        var inputPath = Path.Combine(FixtureRoot, cli, "2417-content-stream.jsonl");
        var expectedPath = Path.Combine(FixtureRoot, "2417-expected-payloads.json");
        var lines = File.ReadAllLines(inputPath, Encoding.UTF8);
        var expected = JsonSerializer.Deserialize<string[]>(File.ReadAllText(expectedPath, Encoding.UTF8))!;

        Assert.Contains(lines, line => line.Length >= 16_384);

        var payloadEvents = lines[..^1]
            .SelectMany(line => Map(cli, line))
            .ToList();

        Assert.All(payloadEvents, evt => Assert.IsType<CliRunEvent.OutputDelta>(evt));
        Assert.DoesNotContain(payloadEvents, evt => evt is
            CliRunEvent.TurnCompleted or
            CliRunEvent.TurnFailed or
            CliRunEvent.NeedsInput or
            CliRunEvent.Interrupt or
            CliRunEvent.RunEnded);
        Assert.Equal(expected, payloadEvents.Cast<CliRunEvent.OutputDelta>().Select(evt => evt.Text));

        var terminalEvents = Map(cli, lines[^1]).ToList();
        Assert.IsType<CliRunEvent.TurnCompleted>(Assert.Single(terminalEvents));
    }

    private static IEnumerable<CliRunEvent> Map(string cli, string line) => cli switch
    {
        "claude" => ClaudeEventAdapter.Map(line, "fixture-run"),
        "codex" => CodexEventAdapter.Map(line, "fixture-run", CliStreamKind.Stdout),
        "gemini" or "antigravity" => GeminiEventAdapter.Map(line, "fixture-run"),
        _ => throw new ArgumentOutOfRangeException(nameof(cli), cli, "Unknown fixture CLI."),
    };

    private sealed class StreamFixtureTheoryAttribute : TheoryAttribute
    {
        public StreamFixtureTheoryAttribute()
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable(OptInVariable),
                    "1",
                    StringComparison.Ordinal))
            {
                Skip = $"Set {OptInVariable}=1 to replay the large CLI stream fixtures.";
            }
        }
    }
}

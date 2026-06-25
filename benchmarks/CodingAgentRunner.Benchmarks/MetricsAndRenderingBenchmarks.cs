using BenchmarkDotNet.Attributes;
using CodingAgentRunner.Metrics;
using CodingAgentRunner.Rendering;

namespace CodingAgentRunner.Benchmarks;

/// <summary>
/// <see cref="UsageSummaryParser.Parse"/> turns a per-turn usage line back into token
/// figures for the metrics recorder; it runs once per <c>TurnCompleted</c>.
/// </summary>
[MemoryDiagnoser]
public class UsageParsingBenchmarks
{
    private const string Summary = "input=1200 output=380 cached=900 reasoning=64 tool_calls=3";

    [Benchmark]
    public long Parse()
    {
        var t = UsageSummaryParser.Parse(Summary);
        return t.Input + t.Output + t.Cached + t.Reasoning;
    }
}

/// <summary>
/// The optional Rendering package: Markdown agent output to the span/line model,
/// then a line to HTML. A UI consumer pays this once per rendered message; the
/// core event-stream consumer never touches it.
/// </summary>
[MemoryDiagnoser]
public class RenderingBenchmarks
{
    private const string Markdown =
        "Here's the plan:\n\n" +
        "1. Refactor `Parser.cs` — see [the file](src/Parser.cs)\n" +
        "2. Add tests in **CodingAgentRunner.Tests**\n" +
        "3. Track it as ASS-1234 and check https://example.com/docs\n\n" +
        "```csharp\nvar events = ClaudeEventAdapter.Map(line, runId);\n```\n";

    private IReadOnlyList<RenderedLine> _lines = System.Array.Empty<RenderedLine>();

    [GlobalSetup]
    public void Setup() => _lines = MarkdownRenderer.ToLines(Markdown);

    [Benchmark]
    public int MarkdownToLines() => MarkdownRenderer.ToLines(Markdown).Count;

    [Benchmark]
    public int LinesToHtml()
    {
        var n = 0;
        foreach (var line in _lines)
            n += HtmlRenderer.SpansToHtml(line).Length;
        return n;
    }
}

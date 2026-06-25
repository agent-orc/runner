using BenchmarkDotNet.Attributes;
using CodingAgentRunner.Adapters;
using CodingAgentRunner.Events;

namespace CodingAgentRunner.Benchmarks;

/// <summary>
/// Throughput of the per-CLI stream-json adapters: how fast a line of agent output
/// becomes typed <see cref="CliRunEvent"/>s. This is the hottest parse path in the
/// library — it runs once per output line for the whole length of a run, so its
/// per-line cost and allocation profile are what scale with a long agent session.
/// </summary>
[MemoryDiagnoser]
public class AdapterParsingBenchmarks
{
    // A representative transcript per CLI: session start, assistant text, a tool
    // call, a tool result / plan update, and the terminal completion frame with
    // its usage line. Shapes match the adapters' real branches (not the Unknown
    // fallback), so the numbers reflect the parse paths a real run actually hits.
    private static readonly string[] ClaudeTranscript =
    {
        """{"type":"system","subtype":"init","session_id":"abc-123"}""",
        """{"type":"assistant","message":{"content":[{"type":"text","text":"Let me read the parser and refactor it."}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/repo/src/Parser.cs"}}]}}""",
        """{"type":"user","message":{"content":[{"type":"tool_result","is_error":false,"content":"public class Parser { }\nmore lines"}]}}""",
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"TodoWrite","input":{"todos":[{"content":"Write the parser","status":"in_progress"},{"content":"Add tests","status":"pending"}]}}]}}""",
        """{"type":"result","is_error":false,"usage":{"input_tokens":1200,"output_tokens":380,"cache_read_input_tokens":900}}""",
    };

    private static readonly string[] CodexTranscript =
    {
        """{"type":"thread.started","thread_id":"t-9"}""",
        """{"type":"turn.started"}""",
        """{"type":"item.completed","item":{"type":"reasoning"}}""",
        """{"type":"item.completed","item":{"type":"agent_message","text":"Refactoring the parser now."}}""",
        """{"type":"item.completed","item":{"type":"command_call","command":"dotnet build"}}""",
        """{"type":"turn.completed","usage":{"input_tokens":1100,"output_tokens":420}}""",
    };

    private static readonly string[] GeminiTranscript =
    {
        """{"type":"init","session_id":"g-1"}""",
        """{"type":"message","role":"assistant","content":"Let me look at the file."}""",
        """{"type":"tool_result","name":"read_file","output":"line1\nline2"}""",
        """{"type":"message","role":"assistant","content":"Done refactoring."}""",
        """{"type":"result","status":"success","stats":{"input_tokens":900,"output_tokens":300}}""",
    };

    [Benchmark(Baseline = true)]
    public int Claude() => Fold(ClaudeTranscript, ClaudeEventAdapter.Map);

    [Benchmark]
    public int Codex() => Fold(CodexTranscript, CodexEventAdapter.Map);

    [Benchmark]
    public int Gemini() => Fold(GeminiTranscript, GeminiEventAdapter.Map);

    private static int Fold(string[] transcript, Func<string, string, IEnumerable<CliRunEvent>> map)
    {
        var count = 0;
        foreach (var line in transcript)
        {
            foreach (var _ in map(line, "run-1"))
                count++;
        }
        return count;
    }
}

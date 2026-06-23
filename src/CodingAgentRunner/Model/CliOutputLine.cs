namespace CodingAgentRunner.Model;

/// <summary>One captured line of a CLI process's output, tagged with its stream.</summary>
public record CliOutputLine
{
    /// <summary>UTC instant the line was captured.</summary>
    public DateTime Timestamp { get; init; }
    /// <summary><c>stdout</c> | <c>stderr</c>.</summary>
    public string Stream { get; init; } = "stdout";
    /// <summary>The captured line text.</summary>
    public string Text { get; init; } = "";
}

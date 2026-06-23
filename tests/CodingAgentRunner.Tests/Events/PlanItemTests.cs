using CodingAgentRunner.Events;
using Xunit;

namespace CodingAgentRunner.Tests.Events;

public class PlanItemTests
{
    [Fact]
    public void PlanItemId_IsStableAcrossWhitespaceAndCaseReformatting()
    {
        var a = PlanItemId.From("Add the parser");
        var b = PlanItemId.From("  add   the   PARSER ");
        Assert.Equal(a, b);
        Assert.Equal(8, a.Length); // 4 bytes -> 8 hex chars
    }

    [Fact]
    public void PlanItemId_DiffersForDifferentTitles()
    {
        Assert.NotEqual(PlanItemId.From("Add the parser"), PlanItemId.From("Remove the parser"));
    }

    [Theory]
    [InlineData("in_progress", "active")]
    [InlineData("in-progress", "active")]
    [InlineData("running", "active")]
    [InlineData("completed", "done")]
    [InlineData("done", "done")]
    [InlineData("pending", "pending")]
    [InlineData("something-else", "pending")]   // unknown never reads as "active"
    [InlineData(null, "pending")]
    public void PlanItemStatus_NormalizesToThreeStateVocabulary(string? raw, string expected)
    {
        Assert.Equal(expected, PlanItemStatus.Normalize(raw));
    }
}

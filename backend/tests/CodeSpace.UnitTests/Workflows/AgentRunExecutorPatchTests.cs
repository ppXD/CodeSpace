using CodeSpace.Core.Services.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins <see cref="AgentRunExecutor.TruncatePatch"/> — the cap that keeps a runaway / binary diff from
/// bloating the persisted run row (read on every resume). Under the cap the diff is verbatim; over it,
/// the head is kept and a marker appended so the truncation is visible.
/// </summary>
[Trait("Category", "Unit")]
public class AgentRunExecutorPatchTests
{
    [Theory]
    [InlineData("", 100)]
    [InlineData("a small diff", 100)]
    [InlineData("exactly-ten!", 12)]   // length == cap → kept verbatim
    public void Keeps_a_patch_within_the_cap_verbatim(string patch, int max) =>
        AgentRunExecutor.TruncatePatch(patch, max).ShouldBe(patch);

    [Fact]
    public void Truncates_an_oversized_patch_keeping_the_head_and_appending_a_marker()
    {
        var big = new string('x', 5000);

        var result = AgentRunExecutor.TruncatePatch(big, 100);

        result[..100].ShouldBe(new string('x', 100), "exactly the first maxChars of the diff are kept as the head");
        result.ShouldContain("truncated", customMessage: "the truncation is signalled, never silent");
        result.ShouldContain("5000", customMessage: "the marker names the original length so the reader knows how much was dropped");
        result.Length.ShouldBeLessThan(big.Length, "a pathological full diff is capped well below its original size");
    }
}

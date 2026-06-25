using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// <see cref="LlmUsage.Add"/> — the accumulator that totals the SEVERAL billed sub-calls one structured completion can
/// make (a forced tool/function attempt that degrades to a prompt-only floor, each re-asked once). Pins the null-aware
/// token sum (a reported count is never lost to a missing one; two missing stay missing) and the finish-reason
/// precedence (the later/accepted sub-call wins, falling back to the earlier).
/// </summary>
[Trait("Category", "Unit")]
public class LlmUsageTests
{
    [Theory]
    [InlineData(10, 5, 3, 2, 13, 7)]      // both report → sum
    [InlineData(10, 5, null, null, 10, 5)] // later reports nothing → keep the earlier (treated as +0)
    [InlineData(null, null, 3, 2, 3, 2)]   // earlier reported nothing → take the later
    public void Add_sums_tokens_treating_a_missing_count_as_zero(int? aIn, int? aOut, int? bIn, int? bOut, int expectIn, int expectOut)
    {
        var sum = new LlmUsage { InputTokens = aIn, OutputTokens = aOut }.Add(new LlmUsage { InputTokens = bIn, OutputTokens = bOut });

        sum.InputTokens.ShouldBe(expectIn);
        sum.OutputTokens.ShouldBe(expectOut);
    }

    [Fact]
    public void Add_keeps_both_null_when_neither_side_reports_a_count()
    {
        var sum = LlmUsage.None.Add(LlmUsage.None);

        sum.InputTokens.ShouldBeNull("two un-reported sub-calls stay un-reported — never a fabricated 0");
        sum.OutputTokens.ShouldBeNull();
    }

    [Fact]
    public void Add_takes_the_accepted_later_finish_reason_unconditionally_even_when_null()
    {
        new LlmUsage { FinishReason = "tool_use" }.Add(new LlmUsage { FinishReason = "end_turn" }).FinishReason.ShouldBe("end_turn", "the accepted (later) sub-call's reason wins");

        // The accepted call reporting no reason → null, NEVER the degraded earlier attempt's reason (surfacing a
        // "tool_use" that produced no usable JSON would mislead a consumer about why the ANSWER actually stopped).
        new LlmUsage { FinishReason = "tool_use" }.Add(LlmUsage.None).FinishReason.ShouldBeNull("the degraded earlier reason must not surface in place of the accepted call's");
    }
}

using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// The shared model-completion finish classifier both the model-call timeline beat and the fact row read: a length-cap
/// (max_tokens / length) is TRUNCATED, a content_filter is FILTERED, and every clean stop / null / unknown reason is
/// CLEAN, so a new provider reason never false-alarms.
/// </summary>
[Trait("Category", "Unit")]
public class ModelCallFinishTests
{
    [Theory]
    [InlineData("max_tokens", ModelCallFinishKind.Truncated)]
    [InlineData("length", ModelCallFinishKind.Truncated)]
    [InlineData("MAX_TOKENS", ModelCallFinishKind.Truncated)]   // case-insensitive
    [InlineData("content_filter", ModelCallFinishKind.Filtered)]
    [InlineData("end_turn", ModelCallFinishKind.Clean)]
    [InlineData("stop", ModelCallFinishKind.Clean)]
    [InlineData("tool_use", ModelCallFinishKind.Clean)]
    [InlineData("tool_calls", ModelCallFinishKind.Clean)]
    [InlineData("stop_sequence", ModelCallFinishKind.Clean)]
    [InlineData(null, ModelCallFinishKind.Clean)]              // unreported → clean, never a false truncation
    [InlineData("some_future_reason", ModelCallFinishKind.Clean)]
    public void Classifies_the_provider_finish_reason(string? finishReason, ModelCallFinishKind expected)
    {
        ModelCallFinish.Classify(finishReason).ShouldBe(expected);
    }

    [Theory]
    [InlineData(ModelCallFinishKind.Clean, null)]
    [InlineData(ModelCallFinishKind.Truncated, "output truncated")]
    [InlineData(ModelCallFinishKind.Filtered, "content filtered")]
    public void Qualifies_a_cut_off_finish_for_the_beat_and_the_row(ModelCallFinishKind kind, string? expected)
    {
        ModelCallFinish.Qualifier(kind).ShouldBe(expected);
    }
}

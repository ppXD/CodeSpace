using CodeSpace.Core.Services.Workflows.Llm;
using Shouldly;

namespace CodeSpace.UnitTests.Workflows;

/// <summary>
/// Pins the generic text-stream fold — the "buffered = accumulate the streaming enumerable" identity that lets a
/// CompleteAsync be exactly a fold over the provider-normalized <see cref="LlmStreamEvent"/> sequence. TextDelta
/// fragments concat in order; Meta fields are last-write-wins PER FIELD (a null field is a no-op, never a clear); an
/// empty stream yields an empty completion carrying the caller's fallback model. This is the invariant PR1's refactor
/// of the OpenAI + Anthropic streaming folds must preserve byte-identically.
/// </summary>
[Trait("Category", "Unit")]
public class LlmTextStreamFoldTests
{
    private static async IAsyncEnumerable<LlmStreamEvent> Seq(params LlmStreamEvent[] events)
    {
        foreach (var e in events) yield return e;
        await Task.CompletedTask;
    }

    [Fact]
    public async Task It_concatenates_text_deltas_and_applies_last_write_wins_meta()
    {
        var completion = await LlmTextStreamFold.AccumulateAsync(Seq(
            new LlmStreamEvent.Meta(Model: "m-start"),
            new LlmStreamEvent.TextDelta("hello "),
            new LlmStreamEvent.TextDelta("there"),
            new LlmStreamEvent.Meta(FinishReason: "stop"),
            new LlmStreamEvent.Meta(InputTokens: 5, OutputTokens: 3),
            new LlmStreamEvent.Meta(Model: "m-final")
        ), fallbackModel: "fb", CancellationToken.None);

        completion.Text.ShouldBe("hello there", "text deltas concat in arrival order");
        completion.Model.ShouldBe("m-final", "the last non-null model wins");
        completion.Usage.InputTokens.ShouldBe(5);
        completion.Usage.OutputTokens.ShouldBe(3);
        completion.Usage.FinishReason.ShouldBe("stop");
    }

    [Fact]
    public async Task An_empty_stream_folds_to_an_empty_completion_with_the_fallback_model()
    {
        var completion = await LlmTextStreamFold.AccumulateAsync(Seq(), fallbackModel: "fb", CancellationToken.None);

        completion.Text.ShouldBe("");
        completion.Model.ShouldBe("fb", "no model event ⇒ the caller's fallback model");
        completion.Usage.InputTokens.ShouldBeNull();
        completion.Usage.OutputTokens.ShouldBeNull();
        completion.Usage.FinishReason.ShouldBeNull();
    }

    [Fact]
    public async Task A_null_meta_field_leaves_the_running_value_untouched()
    {
        // A later Meta with a null field must NOT clear a value an earlier Meta set — last-write-wins is PER FIELD, null = no-op.
        var completion = await LlmTextStreamFold.AccumulateAsync(Seq(
            new LlmStreamEvent.Meta(Model: "m", InputTokens: 7),
            new LlmStreamEvent.Meta(OutputTokens: 9)   // Model + InputTokens are null here — they must survive
        ), fallbackModel: "fb", CancellationToken.None);

        completion.Model.ShouldBe("m");
        completion.Usage.InputTokens.ShouldBe(7);
        completion.Usage.OutputTokens.ShouldBe(9);
    }
}

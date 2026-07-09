namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// The ONE generic fold from a provider-normalized <see cref="LlmStreamEvent"/> stream to a whole <see cref="LLMCompletion"/>.
/// Both wire clients (OpenAI + Anthropic, and Custom via delegation) express their buffered CompleteAsync as exactly this
/// fold over their own event enumerable — the "buffered = accumulate the streaming enumerable" identity that keeps every
/// existing buffered caller byte-identical while the deltas become reusable. Delegates the per-event accumulation to the
/// shared <see cref="LlmCompletionAccumulator"/>, so the fold and the recording decorator's live tee can never drift.
/// </summary>
internal static class LlmTextStreamFold
{
    public static async Task<LLMCompletion> AccumulateAsync(IAsyncEnumerable<LlmStreamEvent> events, string fallbackModel, CancellationToken cancellationToken)
    {
        var accumulator = new LlmCompletionAccumulator();

        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
            accumulator.Add(evt);

        return accumulator.Build(fallbackModel);
    }
}

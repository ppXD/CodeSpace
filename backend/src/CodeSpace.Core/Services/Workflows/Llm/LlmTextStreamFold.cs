using System.Text;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// The ONE generic fold from a provider-normalized <see cref="LlmStreamEvent"/> stream to a whole <see cref="LLMCompletion"/>.
/// Both wire clients (OpenAI + Anthropic, and Custom via delegation) express their buffered CompleteAsync as exactly this
/// fold over their own event enumerable — the "buffered = accumulate the streaming enumerable" identity that keeps every
/// existing buffered caller byte-identical while the deltas become reusable. TextDelta fragments concat in order; each
/// Meta field is last-write-wins (a null field is a no-op).
/// </summary>
internal static class LlmTextStreamFold
{
    public static async Task<LLMCompletion> AccumulateAsync(IAsyncEnumerable<LlmStreamEvent> events, string fallbackModel, CancellationToken cancellationToken)
    {
        var text = new StringBuilder();
        string? model = null, finishReason = null;
        int? inputTokens = null, outputTokens = null;

        await foreach (var evt in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            switch (evt)
            {
                case LlmStreamEvent.TextDelta d:
                    text.Append(d.Text);
                    break;

                case LlmStreamEvent.Meta m:
                    if (m.Model is not null) model = m.Model;
                    if (m.InputTokens is not null) inputTokens = m.InputTokens;
                    if (m.OutputTokens is not null) outputTokens = m.OutputTokens;
                    if (m.FinishReason is not null) finishReason = m.FinishReason;
                    break;
            }
        }

        return new LLMCompletion
        {
            Text = text.ToString(),
            Model = model ?? fallbackModel,
            Usage = new LlmUsage { InputTokens = inputTokens, OutputTokens = outputTokens, FinishReason = finishReason },
        };
    }
}

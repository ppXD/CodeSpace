namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Incremental text-streaming capability — a SIBLING of <see cref="ILLMClient"/> (ISP), not a widening of it. A provider
/// that can stream implements this IN ADDITION to <see cref="ILLMClient"/>; callers feature-detect with a cast
/// (<c>client is IStreamingLLMClient</c>), exactly like <see cref="IStructuredLLMClient"/>. Every existing buffered caller
/// is untouched — <see cref="ILLMClient.CompleteAsync"/> stays the whole-completion surface (it internally folds this same
/// event stream when the output is large), while a caller that wants the deltas AS THEY ARRIVE opts into this.
/// </summary>
public interface IStreamingLLMClient
{
    /// <summary>The provider tag this client serves. Matches <see cref="ILLMClient.Provider"/>.</summary>
    string Provider { get; }

    /// <summary>
    /// Stream one completion as a provider-normalized <see cref="LlmStreamEvent"/> sequence (TextDelta fragments + Meta
    /// updates). FORCES streaming — the caller explicitly wants deltas, so this ignores the buffered path's large-output
    /// gate. Fold the sequence with <see cref="LlmTextStreamFold.AccumulateAsync"/> to recover the exact
    /// <see cref="LLMCompletion"/> <see cref="ILLMClient.CompleteAsync"/> would return.
    /// </summary>
    IAsyncEnumerable<LlmStreamEvent> StreamAsync(LLMCompletionRequest request, CancellationToken cancellationToken);
}

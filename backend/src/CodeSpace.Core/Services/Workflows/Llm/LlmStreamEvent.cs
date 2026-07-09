namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// One event in a provider-normalized LLM text-completion stream. Each wire client folds its SSE (OpenAI chunk deltas /
/// Anthropic message events) into THIS sequence, so a buffered <see cref="LLMCompletion"/> is exactly "accumulate the
/// sequence to the end" (see <see cref="LlmTextStreamFold"/>). Two shapes only: an incremental TEXT fragment, or a
/// METADATA update (model id, token counts, finish reason) that arrives at the stream's start/end.
///
/// <para>A closed union — the private ctor means only the nested records derive, so a fold can switch exhaustively.
/// Lives next to <see cref="LlmUsage"/> / <see cref="LLMCompletion"/> (the sibling completion nouns), not in Messages,
/// matching where those already live; a later PR that exposes a streaming client surface can promote the family together.</para>
/// </summary>
public abstract record LlmStreamEvent
{
    private LlmStreamEvent() { }

    /// <summary>An incremental text fragment — appended, in arrival order, to the growing completion text.</summary>
    public sealed record TextDelta(string Text) : LlmStreamEvent;

    /// <summary>A metadata update. Each NON-null field overwrites the running completion metadata (last-write-wins); a null field is a no-op (never a clear), so a later partial Meta can't erase an earlier value.</summary>
    public sealed record Meta(string? Model = null, int? InputTokens = null, int? OutputTokens = null, string? FinishReason = null) : LlmStreamEvent;
}

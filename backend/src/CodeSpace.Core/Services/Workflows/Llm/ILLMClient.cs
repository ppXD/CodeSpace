namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Provider-neutral LLM client surface. One implementation per provider (Anthropic,
/// OpenAI, Bedrock, Vertex …).
///
/// The interface stays narrow on purpose — one method that takes a prompt and returns
/// text. Function-calling, streaming, structured outputs, embeddings all land as SIBLING
/// interfaces (ISP) — adding them later does NOT widen <c>ILLMClient</c>, so existing
/// implementations never have to grow new methods to "fit" the contract.
/// </summary>
public interface ILLMClient
{
    /// <summary>The provider tag this client serves. Matches <see cref="ILLMProviderModule.Provider"/>.</summary>
    string Provider { get; }

    Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// One LLM call. Kept minimal — system message, user message, model id, generation knobs.
/// The provider impl maps these to whichever schema its API wants (Anthropic's messages[]
/// shape, OpenAI's chat-completions shape, …).
/// </summary>
public sealed record LLMCompletionRequest
{
    public required string Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }
    public int MaxOutputTokens { get; init; } = 2048;
    public double Temperature { get; init; } = 0.2;
}

public sealed record LLMCompletion
{
    public required string Text { get; init; }
    public required string Model { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

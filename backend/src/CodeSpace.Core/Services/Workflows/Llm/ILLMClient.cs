using System.Text.Json.Serialization;
using CodeSpace.Messages.Agents;

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

    /// <summary>Optional generation knobs (top_p / penalties / stop) the client maps onto its API's supported params. Null ⇒ none sent (byte-identical to the prior behaviour).</summary>
    public LlmSamplingOptions? Sampling { get; init; }

    /// <summary>The resolved credential (key + base URL) this call authenticates with. Null = the client's operator-global env key. Transient + <c>[JsonIgnore]</c> so the secret never serializes. See <c>StructuredLLMCompletionRequest.Credential</c>.</summary>
    [JsonIgnore]
    public ResolvedModelCredential? Credential { get; init; }
}

public sealed record LLMCompletion
{
    public required string Text { get; init; }
    public required string Model { get; init; }

    /// <summary>Provider-reported token counts + stop reason. Never null — <see cref="LlmUsage.None"/> when the provider returned no usage.</summary>
    public LlmUsage Usage { get; init; } = LlmUsage.None;
}

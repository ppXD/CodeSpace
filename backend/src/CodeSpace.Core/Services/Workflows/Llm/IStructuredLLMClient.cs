using System.Text.Json;

namespace CodeSpace.Core.Services.Workflows.Llm;

/// <summary>
/// Structured-output capability — a SIBLING of <see cref="ILLMClient"/> (ISP), not a widening of it.
/// A provider that can return schema-constrained JSON implements this in ADDITION to
/// <see cref="ILLMClient"/>; one that can't simply doesn't, and callers feature-detect with a cast.
///
/// This is what lets a planning step emit a typed object (e.g. <c>{ "subtasks": [...] }</c>) the rest
/// of the workflow can index into with <c>{{nodes.planner.outputs.json.subtasks[0]}}</c> instead of
/// re-parsing free-text — the difference between a reliable fan-out and a brittle string scrape.
/// </summary>
public interface IStructuredLLMClient
{
    /// <summary>The provider tag this client serves. Matches <see cref="ILLMClient.Provider"/>.</summary>
    string Provider { get; }

    /// <summary>
    /// One LLM call constrained to return JSON matching <see cref="StructuredLLMCompletionRequest.JsonSchema"/>.
    /// The provider impl maps the schema to whatever its API offers (Anthropic forces a single tool whose
    /// input_schema IS the schema; OpenAI would use response_format json_schema, …).
    /// </summary>
    Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken);
}

/// <summary>One structured LLM call. Mirrors <c>LLMCompletionRequest</c> plus the desired output schema.</summary>
public sealed record StructuredLLMCompletionRequest
{
    public required string Model { get; init; }
    public required string SystemPrompt { get; init; }
    public required string UserPrompt { get; init; }

    /// <summary>JSON Schema (object) the response MUST conform to.</summary>
    public required JsonElement JsonSchema { get; init; }

    public int MaxOutputTokens { get; init; } = 2048;
    public double Temperature { get; init; } = 0.2;
}

public sealed record StructuredLLMCompletion
{
    /// <summary>The schema-valid object the model produced.</summary>
    public required JsonElement Json { get; init; }
    public required string Model { get; init; }
    public int? InputTokens { get; init; }
    public int? OutputTokens { get; init; }
}

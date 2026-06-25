using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The HONEST fake at the <see cref="IStructuredLLMClient"/> boundary — the planner half of the headline
/// flow. The <c>llm.complete(responseSchema)</c> planner node resolves THIS client (registered under the
/// distinct provider tag <see cref="ProviderTag"/>, so it sits alongside — not on top of — the real
/// Anthropic client and the registry's duplicate-provider guard stays happy) and gets back a DETERMINISTIC
/// <c>{ "subtasks": [...] }</c> object that the downstream <c>flow.map</c> fans out over.
///
/// <para>It implements the same <see cref="IStructuredLLMClient"/> the production <c>AnthropicClient</c> does,
/// so the node routes through the real structured-output path (the cast + <c>CompleteStructuredAsync</c> call
/// + the parsed-object-on-<c>json</c> mapping) — only the network call to a real model is replaced. The
/// subtasks are fixed (not derived from the prompt) so the whole flow is reproducible across runs.</para>
/// </summary>
public sealed class DeterministicPlannerLlmClient : ILLMClient, IStructuredLLMClient
{
    /// <summary>The provider tag the planner node selects (config <c>provider</c>). Distinct from "Anthropic" so the registry holds BOTH this stub and the real client without a duplicate-provider collision.</summary>
    public const string ProviderTag = "TestPlanner";

    /// <summary>The fixed plan the planner emits — three subtasks the map fans out over, each becoming one real agent branch.</summary>
    public static readonly IReadOnlyList<string> Subtasks = new[] { "alpha", "beta", "gamma" };

    public string Provider => ProviderTag;

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = string.Join(", ", Subtasks), Model = request.Model, Usage = new() { InputTokens = 7, OutputTokens = 9 } });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToElement(new { subtasks = Subtasks });

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, Usage = new() { InputTokens = 11, OutputTokens = 13 } });
    }
}

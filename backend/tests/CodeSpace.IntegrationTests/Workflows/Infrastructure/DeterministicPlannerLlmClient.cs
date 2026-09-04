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
        var json = IsEffortClassification(request.JsonSchema)
            ? ClassifyEffort(request.UserPrompt)
            : JsonSerializer.SerializeToElement(new { subtasks = Subtasks });

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, Usage = new() { InputTokens = 11, OutputTokens = 13 } });
    }

    /// <summary>The team pool makes this client the in-process structured answerer for EVERY caller, so it answers by the SCHEMA it was handed: an effort-classification schema (it declares <c>deliverableShape</c>) is not a plan request and must not be answered with subtasks — the classifier would then bind an all-default reply and read every task as a code change.</summary>
    private static bool IsEffortClassification(JsonElement schema) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("properties", out var props)
        && props.TryGetProperty("deliverableShape", out _);

    /// <summary>A deterministic classification derived from the prompt's own wording — enough for a flow test to prove the SHAPE reaches the projection, with the signals left cheap so the policy routes to the quick tier.</summary>
    private static JsonElement ClassifyEffort(string userPrompt)
    {
        var shape = Contains(userPrompt, "design doc", "report", "proposal") ? "document"
            : Contains(userPrompt, "investigate", "analyse", "analyze", "compare") ? "research"
            : Contains(userPrompt, "explain", "why", "what ", "how does") ? "answer"
            : "code";

        return JsonSerializer.SerializeToElement(new
        {
            needsCodeChange = shape == "code",
            crossFile = false,
            needsTestsOrCi = false,
            ambiguous = false,
            riskySideEffects = false,
            estimatedCostTier = "low",
            deliverableShape = shape,
            // Below EffortPolicy.ConfirmConfidenceFloor on purpose: a deterministic fake has no calibrated belief to
            // report, and the auto path's confirm-card affordance is what these flow tests expect to ride along.
            confidence = 0.4,
            rationale = $"Deterministic fake: read the ask as '{shape}'.",
        });
    }

    private static bool Contains(string text, params string[] needles) =>
        needles.Any(n => text.Contains(n, StringComparison.OrdinalIgnoreCase));
}

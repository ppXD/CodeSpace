using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The HONEST fake at the <see cref="IStructuredLLMClient"/> boundary — the SPEC-PLANNER half of the
/// plan-map-dynamic flow (PR-B). Distinct from <see cref="DeterministicPlannerLlmClient"/> (which emits a flat
/// string array): this client returns an OBJECT-ARRAY <c>{ "subtasks": [{ name, goal, mode }] }</c>, so each
/// fan-out subtask carries a MODEL-CHOSEN <c>mode</c> ∈ {research, code} the dynamic body maps to permissions —
/// the model deciding each agent's intent. A SEPARATE fake under its OWN provider tag (<see cref="ProviderTag"/>)
/// so the existing string-planner fake (and <c>PlanMapSynthFanoutFlowTests</c>) stay untouched.
///
/// <para>It implements the same <see cref="IStructuredLLMClient"/> the production <c>AnthropicClient</c> does, so
/// the planner node routes through the real structured-output path (the cast + <c>CompleteStructuredAsync</c> +
/// the parsed-object-on-<c>json</c> mapping) — only the network call to a real model is replaced. The specs are
/// fixed (not derived from the prompt) so the whole flow is reproducible across runs.</para>
/// </summary>
public sealed class DeterministicSpecPlannerLlmClient : ILLMClient, IStructuredLLMClient
{
    /// <summary>The provider tag the planner node selects (config <c>provider</c>). Distinct from "Anthropic", "TestPlanner", and "TestSynth" so the registry holds them all without a duplicate-provider collision.</summary>
    public const string ProviderTag = "TestSpecPlanner";

    /// <summary>One model-authored per-agent spec: a short name, a self-contained goal, and the model's chosen mode.</summary>
    public sealed record Spec(string Name, string Goal, string Mode);

    /// <summary>
    /// The fixed HETEROGENEOUS-mode plan the planner emits — three subtasks the map fans out over, each becoming
    /// one real agent branch: ONE research (analysis-only) + TWO code (each produces a branch). The goals match the
    /// <see cref="SubtaskAwareFakeCli"/>'s per-branch summary derivation ("Work on &lt;x&gt;").
    /// </summary>
    public static readonly IReadOnlyList<Spec> Specs = new[]
    {
        new Spec("investigate", "Work on alpha", "research"),
        new Spec("implement-a", "Work on beta", "code"),
        new Spec("implement-b", "Work on gamma", "code"),
    };

    public string Provider => ProviderTag;

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = string.Join(", ", Specs.Select(s => s.Goal)), Model = request.Model, InputTokens = 7, OutputTokens = 9 });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToElement(new { subtasks = Specs.Select(s => new { name = s.Name, goal = s.Goal, mode = s.Mode }) });

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, InputTokens = 11, OutputTokens = 13 });
    }
}

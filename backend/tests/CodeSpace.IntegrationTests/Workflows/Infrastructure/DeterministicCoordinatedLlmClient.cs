using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The HONEST fake at the <see cref="IStructuredLLMClient"/> / <see cref="ILLMClient"/> boundary for the L3
/// CHECKPOINT-COORDINATED flow (PR-D.5) — a single instance plays BOTH roles in one coordinated run:
///
/// <list type="bullet">
/// <item><b>Planner</b>: the structured call whose schema has a <c>subtasks</c> property returns the initial
///   <c>PlannedWorkflow</c> (two subtasks) — the round-1 fan-out.</item>
/// <item><b>Coordinator</b>: the structured call whose schema has a <c>decision</c> property returns
///   <c>rework</c> on the FIRST round (with two distinct reworkSubtasks) and <c>done</c> on the SECOND — so
///   the loop runs exactly two iterations and terminates on <c>done</c>.</item>
/// </list>
///
/// <para>The plain-text path (<see cref="CompleteAsync"/>) serves the analysis body + synthesizer nodes,
/// echoing the prompt deterministically. Only the network call to a real model is replaced; the production
/// <c>WorkflowPlanProjector</c>, the real engine (loop re-walk, map wait-for-all, CAS resume), and the real
/// <c>DefinitionValidator</c> are all exercised. The same instance must be registered at the fixture ROOT
/// (the engine's llm.complete nodes resolve it after the test retargets their provider) AND in the planning
/// child scope (the planner resolves it) so the coordinator's round counter is shared across both.</para>
/// </summary>
public sealed class DeterministicCoordinatedLlmClient : ILLMClient, IStructuredLLMClient
{
    /// <summary>Distinct provider tag — sits beside the real Anthropic client at root without a duplicate-provider collision.</summary>
    public const string ProviderTag = "TestCoordinator";

    /// <summary>The initial (round-1) subtask titles the planner emits — two subtasks the round-1 map fans over.</summary>
    public static readonly IReadOnlyList<string> PlanSubtaskTitles = new[] { "Draft", "Review" };

    /// <summary>The round-2 (rework) subtask titles the coordinator emits — distinct from round 1 so the test can prove the map fanned over the REWORK set.</summary>
    public static readonly IReadOnlyList<string> ReworkSubtaskTitles = new[] { "Revise A", "Revise B", "Revise C" };

    private int _coordinatorCalls;

    public string Provider => ProviderTag;

    /// <summary>How many times the coordinator decided so far — the test asserts this equals the loop's iteration count (2).</summary>
    public int CoordinatorCalls => _coordinatorCalls;

    /// <summary>Reset the round counter so a re-run of the single coordinated test starts clean (the instance is a fixture-wide SingleInstance).</summary>
    public void Reset() => Interlocked.Exchange(ref _coordinatorCalls, 0);

    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = $"done: {request.UserPrompt}", Model = request.Model, InputTokens = 5, OutputTokens = 7 });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var json = SchemaHasProperty(request.JsonSchema, "decision") ? CoordinatorDecisionJson() : PlanJson();

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, InputTokens = 11, OutputTokens = 23 });
    }

    /// <summary>The initial plan — two subtasks, analysis kind (llm.complete body, no sandbox).</summary>
    private static JsonElement PlanJson() => JsonSerializer.SerializeToElement(new
    {
        goal = "Improve the module across rounds",
        subtasks = PlanSubtaskTitles.Select((title, i) => new
        {
            id = $"s{i + 1}",
            title,
            instruction = $"Do the work for {title.ToLowerInvariant()}",
        }).ToArray(),
        recommendedWorkflowKind = "analysis",
    });

    /// <summary>Round 1 ⇒ rework (with reworkSubtasks); round 2 ⇒ done. The counter increments per coordinator call so the two engine rounds get distinct decisions.</summary>
    private JsonElement CoordinatorDecisionJson()
    {
        var round = Interlocked.Increment(ref _coordinatorCalls);

        if (round == 1)
            return JsonSerializer.SerializeToElement(new
            {
                decision = "rework",
                summary = "Round 1 incomplete — another pass needed.",
                reworkSubtasks = ReworkSubtaskTitles.Select((title, i) => new
                {
                    id = $"r{i + 1}",
                    title,
                    instruction = $"Rework: {title.ToLowerInvariant()}",
                }).ToArray(),
                riskLevel = "medium",
            });

        return JsonSerializer.SerializeToElement(new
        {
            decision = "done",
            summary = "Round 2 met the goal.",
            riskLevel = "low",
        });
    }

    private static bool SchemaHasProperty(JsonElement schema, string property) =>
        schema.ValueKind == JsonValueKind.Object
        && schema.TryGetProperty("properties", out var props)
        && props.ValueKind == JsonValueKind.Object
        && props.TryGetProperty(property, out _);
}

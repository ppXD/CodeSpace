using System.Text.Json;
using CodeSpace.Core.Services.Workflows.Llm;

namespace CodeSpace.IntegrationTests.Workflows.Infrastructure;

/// <summary>
/// The HONEST fake at the <see cref="IStructuredLLMClient"/> boundary for the TASK PLANNER (PR-D Slice 1) —
/// the planner-shaped sibling of <see cref="DeterministicPlannerLlmClient"/>. The production
/// <c>LlmWorkflowPlanner</c> resolves a structured client through the real registry, calls the real
/// <c>CompleteStructuredAsync</c>, and deserializes the result with the production <c>PlannerSchema.Options</c>
/// — only the network call to a real model is replaced.
///
/// <para>Registered at the fixture root under its OWN provider tag (<see cref="ProviderTag"/>), beside the
/// real Anthropic client and the headline fake — no duplicate-provider collision. The planning call resolves
/// it via a child-scope registry that holds only this client (deterministic), and the projected
/// <c>llm.complete</c> body + synthesizer nodes (singletons holding the ROOT registry) reach it once the test
/// retargets their provider to <see cref="ProviderTag"/> — so the whole plan → project → fan-out → synthesize
/// flow runs with no API key, faking ONLY the LLM at the honest ILLMClient / IStructuredLLMClient seam.</para>
/// </summary>
public sealed class DeterministicTaskPlannerLlmClient : ILLMClient, IStructuredLLMClient
{
    /// <summary>Distinct provider tag — sits beside the real Anthropic client at root without a duplicate-provider collision.</summary>
    public const string ProviderTag = "TestTaskPlanner";

    /// <summary>The fixed subtask titles the planner emits — three subtasks the projected map fans out over.</summary>
    public static readonly IReadOnlyList<string> SubtaskTitles = new[] { "Audit", "Refactor", "Verify" };

    /// <summary>The <c>recommendedWorkflowKind</c> the projector switches a CODING plan on (→ an agent.run body).</summary>
    public const string CodingKind = "coding";

    /// <summary>The default <c>recommendedWorkflowKind</c> — the analysis path (→ an llm.complete body).</summary>
    public const string AnalysisKind = "analysis";

    private readonly string _recommendedWorkflowKind;

    /// <summary>Default ctor — Autofac resolves this at the fixture root, emitting the ANALYSIS path (the byte-identical behaviour the existing PlannerProjectionFlowTests + retarget seam depend on).</summary>
    public DeterministicTaskPlannerLlmClient() : this(AnalysisKind) { }

    /// <summary>Kind-selecting ctor — a test instantiates this in its own child-scope registry to drive the CODING projection path (<see cref="CodingKind"/> ⇒ agent.run body).</summary>
    public DeterministicTaskPlannerLlmClient(string recommendedWorkflowKind) { _recommendedWorkflowKind = recommendedWorkflowKind; }

    public string Provider => ProviderTag;

    /// <summary>Plain-text path — used by the projected body (analysis) + synthesizer llm.complete nodes. Echoes the prompt deterministically.</summary>
    public Task<LLMCompletion> CompleteAsync(LLMCompletionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(new LLMCompletion { Text = $"done: {request.UserPrompt}", Model = request.Model, Usage = new() { InputTokens = 5, OutputTokens = 7 } });

    public Task<StructuredLLMCompletion> CompleteStructuredAsync(StructuredLLMCompletionRequest request, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToElement(new
        {
            goal = "Improve the module",
            subtasks = SubtaskTitles.Select((title, i) => new
            {
                id = $"s{i + 1}",
                title,
                instruction = $"Do the work for {title.ToLowerInvariant()}",
            }).ToArray(),
            successCriteria = new[] { "All subtasks complete" },
            risks = new[] { "Unknowns surface mid-run" },
            recommendedWorkflowKind = _recommendedWorkflowKind,
        });

        return Task.FromResult(new StructuredLLMCompletion { Json = json, Model = request.Model, Usage = new() { InputTokens = 11, OutputTokens = 23 } });
    }
}

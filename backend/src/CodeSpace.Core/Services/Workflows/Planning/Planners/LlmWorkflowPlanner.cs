using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Dtos.Workflows.Planning;

namespace CodeSpace.Core.Services.Workflows.Planning.Planners;

/// <summary>
/// The structured-LLM <see cref="IWorkflowPlanner"/> (Rule 18.3 — an impl in the <c>Planners/</c> variant
/// folder). It resolves a structured-capable LLM client through the SAME <see cref="ILLMClientRegistry"/>
/// the <c>llm.complete</c> node uses, sends a system+user prompt constrained by
/// <see cref="PlannerSchema.ResponseSchema"/>, and deserializes the schema-valid object into a
/// <see cref="PlannedWorkflow"/>. Fails cleanly when no registered provider offers structured output.
///
/// <para>The planner produces DATA only — it never wires nodes or runs anything. The grounding context
/// (when present) is framed honestly as supplementary repo context, never as "I analyzed your codebase".</para>
/// </summary>
public sealed class LlmWorkflowPlanner : IWorkflowPlanner, IScopedDependency
{
    private readonly ILLMClientRegistry _clientRegistry;
    private readonly IModelPoolSelector _modelSelector;
    private readonly IAgentHarnessRegistry _harnesses;

    public LlmWorkflowPlanner(ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector, IAgentHarnessRegistry harnesses)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
        _harnesses = harnesses;
    }

    public async Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
    {
        // Resolve the brain: the operator's pinned model BY ROW ID when set (the same path the supervisor decider uses —
        // selected = verbatim, fail clearly if it isn't a structured-eligible team row); else auto-resolve a structured
        // client + a team pool model that MATCH — iterating the registered structured providers so a team whose pool is
        // ALL one provider (e.g. all Custom-gateway models) plans on THAT provider's client, not a provider-blind pick.
        var resolved = request.BrainModelId is { } brainModelId
            ? await InProcessStructuredModel.ResolveByRowIdAsync(_clientRegistry, _modelSelector, request.TeamId, brainModelId, cancellationToken).ConfigureAwait(false)
            : await InProcessStructuredModel.ResolveAsync(_clientRegistry, _modelSelector, request.TeamId, cancellationToken).ConfigureAwait(false);

        if (resolved is not { } pickedBrain)
            throw new InvalidOperationException(request.BrainModelId is null
                ? "No structured-output LLM provider has a credentialed, enabled model in the team's pool. Add a model whose provider a registered structured client serves (Anthropic / OpenAI / a Custom OpenAI-compatible gateway)."
                : "The pinned brain model is not an enabled, structured-eligible model in the team's pool (missing / disabled / revoked / cross-team, or no registered structured client serves its provider).");

        var (structured, pick) = pickedBrain;

        // P2 — render the capability catalog (harnesses + drivable providers, the team's whole credentialed pool) so the
        // planner allocates a provider-compatible harness + model PER subtask informed, not blind. The run-time
        // reconciler is the backstop.
        var pool = await _modelSelector.ListPoolAsync(request.TeamId, allowedRowIds: null, cancellationToken).ConfigureAwait(false);
        var catalog = CapabilityCatalog.Render(_harnesses.All, pool);

        var completion = await structured.CompleteStructuredAsync(BuildRequest(request, pick, catalog), cancellationToken).ConfigureAwait(false);

        return Deserialize(completion.Json);
    }

    private static StructuredLLMCompletionRequest BuildRequest(WorkflowPlanRequest request, ModelPoolPick pick, string catalog) => new()
    {
        Model = pick.ModelId,
        Credential = pick.Credential,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(request, catalog),
        JsonSchema = PlannerSchema.ResponseSchema,
        MaxOutputTokens = 4096,
        Temperature = 0.2,
    };

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the prompt framing + over-claim guard directly, without a real LLM round-trip.</summary>
    internal static string BuildUserPromptForTest(WorkflowPlanRequest request, string catalog = "") => BuildUserPrompt(request, catalog);

    private static string BuildUserPrompt(WorkflowPlanRequest request, string catalog)
    {
        var builder = new StringBuilder();

        builder.AppendLine("Task to plan:");
        builder.AppendLine(request.TaskText);

        if (!string.IsNullOrWhiteSpace(request.GroundingContext))
        {
            builder.AppendLine();
            builder.AppendLine("Repository top-level layout (use it to ground the plan). This is a top-level listing only — it is NOT a full code analysis, so do not assume anything below the named entries:");
            builder.AppendLine(request.GroundingContext);
        }

        if (!string.IsNullOrWhiteSpace(catalog))
        {
            builder.AppendLine();
            builder.AppendLine(catalog.TrimEnd());
        }

        // IMPROVE: an independent reviewer critiqued a prior draft of this plan — revise to address it (set by the
        // CriticPlannerDecorator on its one re-plan; absent on a first pass).
        if (!string.IsNullOrWhiteSpace(request.ReviewerCritique))
        {
            builder.AppendLine();
            builder.AppendLine("An independent reviewer critiqued a PRIOR draft of this plan. Produce an improved plan that addresses this critique:");
            builder.AppendLine(request.ReviewerCritique);
        }

        return builder.ToString();
    }

    private static PlannedWorkflow Deserialize(JsonElement json)
    {
        var plan = json.Deserialize<PlannedWorkflow>(PlannerSchema.Options);

        if (plan == null || plan.Subtasks.Count == 0)
            throw new InvalidOperationException("The planner returned an empty plan (no subtasks). The response did not conform to the planner schema.");

        return plan;
    }

    private const string SystemPrompt =
        "You are a senior engineer turning a free-text task into a concrete, reviewable plan. " +
        "Break the task into a small number of ordered, independently-executable subtasks (1–20). " +
        "Give each subtask a stable id, a short title, and a concrete instruction. " +
        "State the success criteria a reviewer would check and the main risks. " +
        "Set recommendedWorkflowKind to 'coding' when the subtasks are code changes a coding agent should make, " +
        "otherwise 'analysis'. " +
        "When a capability catalog is provided, you MAY give each subtask its best-fit harness + model from it — pick a " +
        "model from the listed pool and a harness whose providers can drive that model's provider; omit them to use the " +
        "run defaults. Return ONLY the schema-constrained JSON.";
}

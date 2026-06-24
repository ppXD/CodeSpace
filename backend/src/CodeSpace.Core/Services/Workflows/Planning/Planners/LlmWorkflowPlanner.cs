using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
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

    public LlmWorkflowPlanner(ILLMClientRegistry clientRegistry, IModelPoolSelector modelSelector)
    {
        _clientRegistry = clientRegistry;
        _modelSelector = modelSelector;
    }

    public async Task<PlannedWorkflow> PlanAsync(WorkflowPlanRequest request, CancellationToken cancellationToken)
    {
        var structured = ResolveStructuredClient();

        var pick = await _modelSelector.SelectAsync(request.TeamId, structured.Provider, allowedModels: null, pinnedModel: null, cancellationToken).ConfigureAwait(false);

        if (pick == null)
            throw new InvalidOperationException($"No model is available in the team's pool for provider '{structured.Provider}'. Add a credentialed, enabled model to plan tasks.");

        var completion = await structured.CompleteStructuredAsync(BuildRequest(request, pick), cancellationToken).ConfigureAwait(false);

        return Deserialize(completion.Json);
    }

    /// <summary>The first registered client that ALSO offers structured output (ISP feature-detect via cast). A deployment with only a plain-text provider gets a clean failure, not a malformed plan.</summary>
    private IStructuredLLMClient ResolveStructuredClient()
    {
        var structured = _clientRegistry.All.OfType<IStructuredLLMClient>().FirstOrDefault();

        if (structured == null)
            throw new InvalidOperationException("No structured-output-capable LLM provider is registered. The task planner needs a provider that supports schema-constrained JSON.");

        return structured;
    }

    private static StructuredLLMCompletionRequest BuildRequest(WorkflowPlanRequest request, ModelPoolPick pick) => new()
    {
        Model = pick.ModelId,
        Credential = pick.Credential,
        SystemPrompt = SystemPrompt,
        UserPrompt = BuildUserPrompt(request),
        JsonSchema = PlannerSchema.ResponseSchema,
        MaxOutputTokens = 4096,
        Temperature = 0.2,
    };

    /// <summary>Internal test accessor (InternalsVisibleTo) — pins the prompt framing + over-claim guard directly, without a real LLM round-trip.</summary>
    internal static string BuildUserPromptForTest(WorkflowPlanRequest request) => BuildUserPrompt(request);

    private static string BuildUserPrompt(WorkflowPlanRequest request)
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
        "otherwise 'analysis'. Return ONLY the schema-constrained JSON.";
}

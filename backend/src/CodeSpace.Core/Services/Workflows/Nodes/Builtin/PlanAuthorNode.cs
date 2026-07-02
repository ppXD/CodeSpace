using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// The graph-tier plan producer (triad slice S1): turns a goal into a structured, DURABLE plan — a
/// <c>work_plan</c> version whose items carry the full contract (instruction, DAG edges, per-item objective
/// acceptance). A thin shell over <see cref="IWorkflowPlanner"/> (so the independent plan critic — the
/// <c>CriticPlannerDecorator</c> — rides along via <c>reviewMode</c>) plus the work-plan store.
///
/// <para>The node PRODUCES only; it never gates. The confirmation pause is a COMPOSED downstream
/// <c>flow.decision</c> (S3), and the edit loop is a graph cycle back into this node with the operator's
/// feedback bound to the <c>feedback</c> input — each re-entry persists the run's NEXT plan version.
/// Downstream fan-out binds the plan the same way it binds a structured <c>llm.complete</c>:
/// <c>{{nodes.&lt;id&gt;.outputs.json.subtasks}}</c> (or the item-shaped <c>outputs.items</c>).</para>
/// </summary>
public sealed class PlanAuthorNode : INodeRuntime
{
    // DI SINGLETON like every node — the scoped planner + store (both hold the scoped DbContext) are resolved
    // from a FRESH scope per RunAsync (the LlmCompleteNode / AgentSupervisorNode pattern) so concurrent
    // branches never share a DbContext.
    private readonly IServiceScopeFactory _scopeFactory;

    public PlanAuthorNode(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string TypeKey => "plan.author";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Author plan",
        Category = "AI",
        Kind = NodeKind.Regular,
        IconKey = "list",
        Description = "Turns a goal into a structured, durable plan (items + dependencies + per-item acceptance) the run's checklist, confirmation gate, and fan-out all read.",
        // One structured LLM call per execution — billing side effects, like llm.complete.
        IsSideEffecting = true,
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "plannerModelId": { "type": "string", "format": "uuid", "x-selector": "credentialedModel", "description": "The credentialed model the planner reasons on. Leave empty to auto-pick the team's strongest structured-eligible model." },
                "reviewMode": { "type": "integer", "enum": [0, 1, 2], "default": 0, "description": "Independent reviewer over the produced plan: 0 = off, 1 = Gate (flag concerns onto the plan's risks), 2 = Improve (one bounded revision against the critique)." },
                "reviewerModelId": { "type": "string", "format": "uuid", "x-selector": "credentialedModel", "description": "The credentialed model the plan reviewer runs on (ideally distinct from the planner). Leave empty to auto-pick. Only used when reviewMode is not 0." }
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "goal": { "type": "string", "minLength": 1, "description": "The task to plan — free text, usually {{ref}}'d from the trigger." },
                "grounding": { "type": "string", "description": "Optional supplementary context folded into the planner prompt (e.g. an upstream node's repo/summary output)." },
                "feedback": { "type": "string", "description": "Optional operator feedback on a PRIOR plan version — bind it on the edit-loop edge so the re-plan revises against it." }
              },
              "required": ["goal"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "planId": { "type": "string", "description": "The persisted work_plan row id of THIS plan version." },
                "version": { "type": "integer", "description": "This plan's version within the run (1-based; re-entries bump it)." },
                "goal": { "type": "string", "description": "The planner's restated goal." },
                "items": { "type": "array", "description": "The persisted plan items — {id, title, instruction, rationale?, dependsOn?, acceptance?, harness?, model?} — bindable as flow.map items." },
                "executionNeeded": { "type": "boolean", "description": "false when the planner declared the goal needs no execution (hasEnoughContext) — a downstream logic.if can route straight to synthesis." },
                "json": { "type": "object", "description": "The raw structured plan (goal/subtasks/successCriteria/risks/recommendedWorkflowKind) — binding-compatible with a structured llm.complete's 'json' output." }
              }
            }
            """)
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var goal = ReadString(context.Inputs, "goal");
        var grounding = ReadString(context.Inputs, "grounding");
        var feedback = ReadString(context.Inputs, "feedback");

        if (string.IsNullOrWhiteSpace(goal)) return NodeResult.Fail("Input 'goal' is required.");

        if (!NodeScopeReader.TryReadTeamId(context, out var teamId))
            return NodeResult.Fail("The run carries no team context — plan.author resolves its planner model from the team's pool.");

        if (!NodeScopeReader.TryReadWorkflowRunId(context, out var workflowRunId))
            return NodeResult.Fail("The run carries no run id — plan.author persists the plan against the run.");

        var request = BuildPlanRequest(context.Config, teamId, goal, grounding, feedback);

        using var scope = _scopeFactory.CreateScope();

        PlannedWorkflow plan;
        try
        {
            plan = await context.Observability.TraceExternalCallAsync(
                target: "planner:structured",
                method: "plan",
                requestPayload: BuildRequestPayloadAudit(goal, grounding, feedback, request.Review),
                action: ct => scope.ServiceProvider.GetRequiredService<IWorkflowPlanner>().PlanAsync(request, ct),
                completionExtractor: p => new ExternalCallCompletion { ResponsePayload = JsonSerializer.SerializeToElement(new { subtask_count = p.Subtasks.Count, has_enough_context = p.HasEnoughContext }) },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            // The planner's clean refusals (no structured-eligible model / a non-conformant empty plan) — a
            // legible node failure, not a crash.
            return NodeResult.Fail(ex.Message);
        }

        var saved = await scope.ServiceProvider.GetRequiredService<IWorkPlanService>().SaveVersionAsync(new WorkPlanDraft
        {
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            OriginKind = WorkPlanOrigins.Node,
            // Deliberately NO origin key: every execution (first pass, edit-loop re-entry) is a NEW version.
            // That inherits the engine's at-least-once boundary for side-effecting nodes — a crash between
            // this commit and node.completed re-executes the node and inserts a phantom next version (plus a
            // second billed planner call). Accepted: highest-version-wins keeps every reader coherent, and the
            // re-executed node's outputs bind ITS version; a stable per-execution key would break the
            // version-per-re-entry edit-loop contract.
            OriginKey = null,
            Goal = plan.Goal,
            Items = plan.Subtasks.Select(WorkPlanItem.From).ToList(),
            SuccessCriteria = plan.SuccessCriteria,
            Risks = plan.Risks,
        }, cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation("plan.author persisted work plan {PlanId} v{Version} with {Items} item(s) (executionNeeded={ExecutionNeeded})", saved.Id, saved.Version, plan.Subtasks.Count, !plan.HasEnoughContext);

        return NodeResult.Ok(BuildOutputs(saved.Id, saved.Version, plan, saved.ItemsJson));
    }

    /// <summary>Map config → the planner request. The feedback (when present) rides the task text so EVERY planner backend revises against it without a contract change; defensive reads per the node convention (an out-of-range reviewMode degrades to off, never throws).</summary>
    internal static WorkflowPlanRequest BuildPlanRequest(IReadOnlyDictionary<string, JsonElement> config, Guid teamId, string goal, string grounding, string feedback) => new()
    {
        TaskText = ComposeTaskText(goal, feedback),
        TeamId = teamId,
        GroundingContext = string.IsNullOrWhiteSpace(grounding) ? null : grounding,
        BrainModelId = ReadGuid(config, "plannerModelId"),
        Review = ReadReviewMode(config),
        ReviewerModelId = ReadGuid(config, "reviewerModelId"),
    };

    /// <summary>The edit-loop fold: operator feedback is appended to the goal as an explicit revision instruction (mirrors how the critic's critique folds into the planner prompt).</summary>
    internal static string ComposeTaskText(string goal, string feedback) =>
        string.IsNullOrWhiteSpace(feedback) ? goal : $"{goal}\n\nThe operator reviewed a PRIOR version of this plan and asked for changes. Revise the plan to address this feedback:\n{feedback}";

    private static Dictionary<string, JsonElement> BuildOutputs(Guid planId, int version, PlannedWorkflow plan, string itemsJson)
    {
        // The PERSISTED items bytes (one serialization, AgentJson camelCase) — outputs and store can't drift.
        using var items = JsonDocument.Parse(itemsJson);

        return new()
        {
            ["planId"] = JsonSerializer.SerializeToElement(planId),
            ["version"] = JsonSerializer.SerializeToElement(version),
            ["goal"] = JsonSerializer.SerializeToElement(plan.Goal),
            ["items"] = items.RootElement.Clone(),
            ["executionNeeded"] = JsonSerializer.SerializeToElement(!plan.HasEnoughContext),
            // AgentJson (camelCase + string enums) — NOT PlannerSchema.Options (a read-side option set with no naming
            // policy, which would emit Pascal keys and break the {{nodes.x.outputs.json.subtasks}} binding contract.
            ["json"] = JsonSerializer.SerializeToElement(plan, AgentJson.Options),
        };
    }

    /// <summary>The redacted ledger summary — sizes + knobs, never the goal text itself (it can carry tenant data; the resolved-inputs path already records it redacted).</summary>
    private static JsonElement BuildRequestPayloadAudit(string goal, string grounding, string feedback, ReviewMode review) =>
        JsonSerializer.SerializeToElement(new
        {
            goal_chars = goal.Length,
            grounding_chars = grounding.Length,
            feedback_chars = feedback.Length,
            review_mode = (int)review,
        });

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static Guid? ReadGuid(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;

    private static ReviewMode ReadReviewMode(IReadOnlyDictionary<string, JsonElement> config) =>
        config.TryGetValue("reviewMode", out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var mode) && Enum.IsDefined(typeof(ReviewMode), mode)
            ? (ReviewMode)mode
            : ReviewMode.None;
}

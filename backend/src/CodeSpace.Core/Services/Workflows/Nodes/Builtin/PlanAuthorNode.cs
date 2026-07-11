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
                "plannerModelId": { "type": "string", "format": "uuid", "title": "Planner model", "x-selector": "credentialedModel", "description": "The model the planner reasons on. Leave empty to auto-pick the team's strongest eligible model." },
                "reviewMode": { "type": "integer", "enum": [0, 1, 2], "default": 0, "title": "Review the plan", "x-enumLabels": { "0": "Off", "1": "Gate", "2": "Improve" }, "description": "An independent reviewer over the produced plan. Gate flags concerns onto the plan's risks; Improve makes one bounded revision against the critique." },
                "reviewerModelId": { "type": "string", "format": "uuid", "title": "Reviewer model", "x-selector": "credentialedModel", "x-advanced": true, "description": "The model the reviewer runs on (ideally distinct from the planner). Leave empty to auto-pick. Only used when a review mode is on." },
                "flatPlan": { "type": "boolean", "default": false, "title": "Independent subtasks only", "x-advanced": true, "description": "Constrain the plan to independent subtasks (no dependsOn) — set by parallel fan-out projections (flow.map runs every item concurrently). Authored dependencies are stripped as a fail-safe (logged)." },
                "reviewerAgent": { "type": "boolean", "default": false, "title": "Review against the real repo", "x-advanced": true, "description": "Review the plan with a real independent agent that clones the repository below and verifies it against the actual code, instead of only the in-process model critic. Falls back to the model critic when the agent can't produce a verdict. Only used when a review mode is on AND a repository is set." },
                "repositoryId": { "type": "string", "format": "uuid", "title": "Repository (for grounded review)", "x-selector": "repository", "x-advanced": true, "description": "The repository the plan targets — what the grounded plan reviewer clones (read-only). Only used when \"Review against the real repo\" is on." },
                "pinnedSha": { "type": "string", "title": "Pinned commit", "x-advanced": true, "description": "The exact commit the grounded reviewer clones at (S1 — the launch's immutable base), so the tree the plan is verified against matches what the executing agents materialize. Leave empty for the default-branch tip. Only used when the grounded review is on." }
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "goal": { "type": "string", "minLength": 1, "description": "The task to plan — free text, usually {{ref}}'d from the trigger." },
                "grounding": { "type": "string", "description": "Optional supplementary context folded into the planner prompt (e.g. an upstream node's repo/summary output)." },
                "feedback": { "type": "string", "description": "Optional operator feedback on a PRIOR plan version — bind it on the edit-loop edge so the re-plan revises against it." },
                "criteria": { "type": "array", "items": { "type": "string" }, "description": "The operator's free-text acceptance criteria (definition of done) — folded into the planner prompt so the plan AND its per-item contracts target them (the standard tier's analogue of the supervisor's acceptanceCriteria)." }
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
        var criteria = ReadStringArray(context.Inputs, "criteria");

        if (string.IsNullOrWhiteSpace(goal)) return NodeResult.Fail("Input 'goal' is required.");

        if (!NodeScopeReader.TryReadTeamId(context, out var teamId))
            return NodeResult.Fail("The run carries no team context — plan.author resolves its planner model from the team's pool.");

        if (!NodeScopeReader.TryReadWorkflowRunId(context, out var workflowRunId))
            return NodeResult.Fail("The run carries no run id — plan.author persists the plan against the run.");

        var request = BuildPlanRequest(context.Config, teamId, ComposeGoalWithCriteria(goal, criteria), grounding, feedback, workflowRunId, context.NodeId);

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

        // A flat-plan consumer (a parallel flow.map) cannot honor ordering — the prompt already forbade
        // dependsOn; stripping any that slipped through keeps the persisted CONTRACT truthful to the execution
        // (a checklist must never show "after #1" chips a parallel fan-out ignored).
        if (ReadFlatPlan(context.Config) && plan.Subtasks.Any(t => t.DependsOn is { Count: > 0 }))
        {
            context.Logger.LogWarning("plan.author stripped dependsOn from {Count} subtask(s) — this is a flat plan for a parallel fan-out", plan.Subtasks.Count(t => t.DependsOn is { Count: > 0 }));

            plan = plan with { Subtasks = plan.Subtasks.Select(t => t with { DependsOn = null }).ToList() };
        }

        var items = plan.Subtasks.Select(WorkPlanItem.From).ToList();

        // Fail CLOSED on a structurally contradictory DAG (dup ids / dangling dependsOn / cycle) — the
        // graph-tier twin of the supervisor's plan validator; a broken graph must never become the contract.
        if (WorkPlanItemGraph.Validate(items) is { } graphError) return NodeResult.Fail($"The authored plan is structurally invalid: {graphError}");

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
            Items = items,
            SuccessCriteria = plan.SuccessCriteria,
            Risks = plan.Risks,
            Assumptions = plan.Assumptions,
            Questions = plan.Questions,
        }, cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation("plan.author persisted work plan {PlanId} v{Version} with {Items} item(s) (executionNeeded={ExecutionNeeded})", saved.Id, saved.Version, plan.Subtasks.Count, !plan.HasEnoughContext);

        return NodeResult.Ok(BuildOutputs(saved.Id, saved.Version, plan, saved.ItemsJson));
    }

    /// <summary>The flat-plan constraint line appended to the task text — the planner must author parallel-safe subtasks. Pinned by a unit test (the map projections depend on it).</summary>
    public const string FlatPlanConstraint = "Constraint: author INDEPENDENT subtasks only — they run in PARALLEL, so do NOT use dependsOn.";

    /// <summary>Map config → the planner request. The feedback (when present) rides the task text so EVERY planner backend revises against it without a contract change (a flat plan additionally appends <see cref="FlatPlanConstraint"/>); defensive reads per the node convention (an out-of-range reviewMode degrades to off, never throws). The run/node linkage (D①) lets the grounded plan reviewer land its AgentRun on this node's cell.</summary>
    internal static WorkflowPlanRequest BuildPlanRequest(IReadOnlyDictionary<string, JsonElement> config, Guid teamId, string goal, string grounding, string feedback, Guid? workflowRunId = null, string? nodeId = null) => new()
    {
        TaskText = ReadFlatPlan(config) ? $"{ComposeTaskText(goal, feedback)}\n\n{FlatPlanConstraint}" : ComposeTaskText(goal, feedback),
        TeamId = teamId,
        GroundingContext = string.IsNullOrWhiteSpace(grounding) ? null : grounding,
        BrainModelId = ReadGuid(config, "plannerModelId"),
        Review = ReadReviewMode(config),
        ReviewerModelId = ReadGuid(config, "reviewerModelId"),
        // D① grounded plan review: a real read-only agent verifies the plan against this repository's actual tree.
        ReviewerAgent = ReadBool(config, "reviewerAgent"),
        RepositoryId = ReadGuid(config, "repositoryId"),
        // S1: the launch's immutable base pin — the reviewer clones the SAME commit the executing agents materialize.
        PinnedSha = string.IsNullOrWhiteSpace(ReadString(config, "pinnedSha")) ? null : ReadString(config, "pinnedSha"),
        WorkflowRunId = workflowRunId,
        NodeId = nodeId,
    };

    /// <summary>Defensive bool config read — absent / non-bool ⇒ false, mirroring the node convention.</summary>
    private static bool ReadBool(IReadOnlyDictionary<string, JsonElement> config, string key) =>
        config.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.True;

    /// <summary>Fold the operator's acceptance criteria into the goal (S5b) — the plan AND its per-item contracts must target the operator's definition of done, and the plan critic judges against the same yardstick (its Goal is this task text). Empty ⇒ the goal verbatim (byte-identical). Pinned by a unit test.</summary>
    internal static string ComposeGoalWithCriteria(string goal, IReadOnlyList<string> criteria)
    {
        var kept = criteria.Where(c => !string.IsNullOrWhiteSpace(c)).ToList();

        if (kept.Count == 0) return goal;

        var builder = new System.Text.StringBuilder(goal);
        builder.AppendLine().AppendLine().AppendLine("Acceptance criteria (the operator's definition of done — author subtasks and their acceptance contracts to satisfy these):");
        foreach (var c in kept) builder.AppendLine($"- {c.Trim()}");

        return builder.ToString().TrimEnd();
    }

    /// <summary>The optional string-array input (e.g. criteria) — defensive: absent / non-array / non-string entries read as empty.</summary>
    private static IReadOnlyList<string> ReadStringArray(IReadOnlyDictionary<string, JsonElement> bag, string key)
    {
        if (!bag.TryGetValue(key, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();

        return v.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").ToList();
    }

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

    private static bool ReadFlatPlan(IReadOnlyDictionary<string, JsonElement> config) =>
        config.TryGetValue("flatPlan", out var flat) && flat.ValueKind == JsonValueKind.True;

    private static Guid? ReadGuid(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;

    // Tolerant of a STRING-encoded review mode ("1") as well as a JSON number: the editor stores every enum as a
    // string (SchemaForm's {{ref}} unification), so a Number-only read would drop it and silently revert to Off.
    internal static ReviewMode ReadReviewMode(IReadOnlyDictionary<string, JsonElement> config)
    {
        if (!config.TryGetValue("reviewMode", out var value)) return ReviewMode.None;

        int? mode = value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var n) ? n
            : value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out var s) ? s
            : null;

        return mode is { } m && Enum.IsDefined(typeof(ReviewMode), m) ? (ReviewMode)m : ReviewMode.None;
    }
}

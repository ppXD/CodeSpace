using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Plans;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Workflows.Planning;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows.Planning;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Plans;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Nodes.Builtin;

/// <summary>
/// The GRAPH-TIER plan-confirmation gate (triad S4d) — the static-graph twin of the supervisor's S3 gate,
/// as a SELF-LOOPING node so any tier gets the full deer-flow edit loop without graph cycles: park on the
/// run's CURRENT plan version, and on the operator's answer either RELEASE (approve → the plan flips
/// Confirmed and the node completes with the APPROVED plan's outputs — downstream binds these, so a
/// rejected plan can never fan out) or REVISE (any other answer is feedback the node re-plans against —
/// a NEW WorkPlan version, exactly-once via a revision origin key — then re-parks on the new version).
///
/// <para>Each park is an <c>Action</c> wait whose suspend payload carries <c>kind: "plan-confirm"</c> +
/// the version, so the confirm endpoint (and the run-detail UI) locate it without parsing the definition;
/// the answer rides the SAME authenticated resume as the supervisor card and the chat button. Every pass
/// re-derives its step from the DURABLE state (the WorkPlan store + this version's wait row), so a crash
/// between any two effects replays into the same step: a re-plan is deduped by its origin key, a status
/// flip by the CAS, a re-park by the engine's one-wait-per-(node, iteration) staging.</para>
///
/// <para>Hand-authoring caveats (the projections never produce these): a graph with TWO plan.confirm nodes
/// leaves the confirm endpoint picking an arbitrary pending gate — use one per run; inside a map branch the
/// per-version iteration key overrides the branch key (the same platform caveat as <c>agent.supervisor</c>);
/// a from-node rerun starting AT this node fails legibly (the WorkPlan rows belong to the original run).</para>
/// </summary>
public sealed class PlanConfirmNode : INodeRuntime
{
    /// <summary>The suspend-payload discriminator the confirm endpoint + run-detail UI match on. Pinned by a unit test.</summary>
    public const string WaitPayloadKind = "plan-confirm";

    /// <summary>Revisions allowed before the node fails legibly instead of looping the planner (billing) forever. Overridable via config <c>maxRevisions</c>.</summary>
    public const int DefaultMaxRevisions = 5;

    private readonly IServiceScopeFactory _scopeFactory;

    public PlanConfirmNode(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public string TypeKey => "plan.confirm";

    public NodeManifest Manifest { get; } = new()
    {
        DisplayName = "Confirm plan",
        Category = "Planning",
        Kind = NodeKind.Regular,
        CanSuspend = true,
        IsSideEffecting = true,   // a revision is a billed planner call
        IconKey = "list-checks",
        Description = "Parks the run until a person confirms the current plan. Approve releases execution (downstream binds THIS node's outputs — the approved plan); any other answer is revision feedback the node re-plans against and re-parks. Wire after plan.author.",
        ConfigSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "plannerModelId": { "type": "string", "format": "uuid", "title": "Planner model", "x-selector": "credentialedModel", "description": "The model revisions reason on (mirrors plan.author). Leave empty to auto-pick." },
                "reviewMode": { "type": "integer", "enum": [0, 1, 2], "default": 0, "title": "Review each revision", "x-enumLabels": { "0": "Off", "1": "Gate", "2": "Improve" }, "description": "An independent reviewer over each revised plan — the same critic the original planner ran under." },
                "reviewerModelId": { "type": "string", "format": "uuid", "title": "Reviewer model", "x-selector": "credentialedModel", "x-advanced": true, "description": "The model the revision reviewer runs on. Leave empty to auto-pick." },
                "flatPlan": { "type": "boolean", "default": false, "title": "Independent subtasks only", "x-advanced": true, "description": "Constrain revisions to independent subtasks (no dependsOn) — set by parallel fan-out projections, mirroring the plan.author upstream." },
                "maxRevisions": { "type": "integer", "minimum": 1, "default": 5, "title": "Max revisions", "x-advanced": true, "description": "Revisions allowed before the node fails legibly instead of looping the planner forever." },
                "reviewerAgent": { "type": "boolean", "default": false, "title": "Review against the real repo", "x-advanced": true, "description": "Review each revised plan with a real independent agent that clones the repository below and verifies it against the actual code, instead of only the in-process model critic. Only used when a review mode is on AND a repository is set." },
                "repositoryId": { "type": "string", "format": "uuid", "title": "Repository (for grounded review)", "x-selector": "repository", "x-advanced": true, "description": "The repository the plan targets — what the grounded plan reviewer clones (read-only). Only used when \"Review against the real repo\" is on." }
              }
            }
            """),
        InputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "goal": { "type": "string", "minLength": 1, "description": "The task the plan serves — revisions re-plan this goal against the operator's feedback." },
                "grounding": { "type": "string", "description": "Optional supplementary context folded into revision prompts (mirror the plan.author upstream)." },
                "criteria": { "type": "array", "items": { "type": "string" }, "description": "The operator's acceptance criteria — revisions target the same definition of done as the original plan (mirror the plan.author upstream)." }
              },
              "required": ["goal"]
            }
            """),
        OutputSchema = SchemaBuilder.Parse("""
            {
              "type": "object",
              "properties": {
                "planId":   { "type": "string", "description": "The APPROVED work-plan row id." },
                "version":  { "type": "integer", "description": "The approved version — v1 when approved as authored, higher after revisions." },
                "approved": { "type": "boolean", "description": "Always true on completion — a rejected plan never releases this node." },
                "goal":     { "type": "string" },
                "items":    { "type": "array", "description": "The approved plan's items (the durable contract)." },
                "json":     { "type": "object", "description": "{ subtasks: items } — binding-compatible with plan.author's json output, so a flow.map binds this node identically." }
              }
            }
            """),
    };

    public async Task<NodeResult> RunAsync(NodeRunContext context, CancellationToken cancellationToken)
    {
        var goal = ReadString(context.Inputs, "goal");

        if (string.IsNullOrWhiteSpace(goal)) return NodeResult.Fail("Input 'goal' is required.");

        if (!NodeScopeReader.TryReadTeamId(context, out var teamId))
            return NodeResult.Fail("The run carries no team context — plan.confirm reads the run's plan from the team-scoped store.");

        if (!NodeScopeReader.TryReadWorkflowRunId(context, out var workflowRunId))
            return NodeResult.Fail("The run carries no run id — plan.confirm gates the run's own plan.");

        using var scope = _scopeFactory.CreateScope();
        var plans = scope.ServiceProvider.GetRequiredService<IWorkPlanService>();

        var current = await plans.GetCurrentAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        if (current == null) return NodeResult.Fail("The run has no plan to confirm — wire plan.confirm after plan.author.");

        // Idempotent re-entry: an approved plan releases immediately (a crash after the flip re-derives the completion).
        if (current.Status == WorkPlanStatuses.Confirmed) return NodeResult.Ok(BuildOutputs(current));

        // The answer comes ONLY from THIS version's resolved wait row — never from context.ResumePayload, whose
        // NodeId-keyed slot holds an ARBITRARY one of this node's accumulated resolutions (a stale feedback could
        // swallow a fresh approve). The wait resolution commits before the re-dispatch, so a genuine resume always
        // finds its version-scoped row; a crash re-entry that lost nothing simply re-parks. The same durable-source
        // rule agent.supervisor follows.
        var answer = await ReadResolvedWaitAnswerAsync(scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>(), context, workflowRunId, current.Version, cancellationToken).ConfigureAwait(false);

        if (answer == null) return await ParkAsync(plans, current, teamId, cancellationToken).ConfigureAwait(false);

        if (Approves(answer))
        {
            await plans.SetStatusAsync(current.Id, teamId, WorkPlanStatuses.AwaitingConfirmation, WorkPlanStatuses.Confirmed, cancellationToken).ConfigureAwait(false);
            await plans.SetStatusAsync(current.Id, teamId, WorkPlanStatuses.Authored, WorkPlanStatuses.Confirmed, cancellationToken).ConfigureAwait(false);

            context.Logger.LogInformation("plan.confirm released plan v{Version} for run {RunId} — the operator approved", current.Version, workflowRunId);

            return NodeResult.Ok(BuildOutputs(current));
        }

        return await ReviseAsync(scope.ServiceProvider, context, current, answer, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Revision: the answer is the operator's feedback. Flip the rejected version, re-plan against the feedback
    /// (EXACTLY-ONCE via the revision origin key — a crash-replay of the same answer adopts the already-inserted
    /// version instead of re-billing the planner), then park on the new version. A version whose origin key marks
    /// it as OUR OWN revision skips straight to the park (the crash window between insert and park).
    /// </summary>
    private async Task<NodeResult> ReviseAsync(IServiceProvider services, NodeRunContext context, WorkPlan current, string answer, CancellationToken cancellationToken)
    {
        var plans = services.GetRequiredService<IWorkPlanService>();
        var goal = ReadString(context.Inputs, "goal");
        var teamId = current.TeamId;
        var workflowRunId = current.WorkflowRunId;

        if (current.OriginKey?.StartsWith(RevisionKeyPrefix, StringComparison.Ordinal) == true && current.Status == WorkPlanStatuses.Authored)
            return await ParkAsync(plans, current, teamId, cancellationToken).ConfigureAwait(false);

        if (RevisionDepth(current) >= ReadMaxRevisions(context.Config))
        {
            await plans.SetStatusAsync(current.Id, teamId, WorkPlanStatuses.AwaitingConfirmation, WorkPlanStatuses.Rejected, cancellationToken).ConfigureAwait(false);

            return NodeResult.Fail($"The plan was revised {RevisionDepth(current)} time(s) and rejected again — refine the goal and relaunch. The feedback is recorded on the rejected plan versions.");
        }

        var criteria = context.Inputs.TryGetValue("criteria", out var cv) && cv.ValueKind == JsonValueKind.Array
            ? cv.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").ToList()
            : (IReadOnlyList<string>)Array.Empty<string>();

        var request = PlanAuthorNode.BuildPlanRequest(context.Config, teamId, PlanAuthorNode.ComposeGoalWithCriteria(goal, criteria), ReadString(context.Inputs, "grounding"), feedback: answer, workflowRunId, context.NodeId);

        PlannedWorkflow plan;
        try
        {
            plan = await context.Observability.TraceExternalCallAsync(
                target: "planner:structured",
                method: "revise",
                requestPayload: JsonSerializer.SerializeToElement(new { goal, feedback = answer, revisionOf = current.Version }),
                action: ct => services.GetRequiredService<IWorkflowPlanner>().PlanAsync(request, ct),
                completionExtractor: p => new ExternalCallCompletion { ResponsePayload = JsonSerializer.SerializeToElement(new { subtask_count = p.Subtasks.Count }) },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            return NodeResult.Fail(ex.Message);
        }

        if (ReadFlat(context.Config) && plan.Subtasks.Any(t => t.DependsOn is { Count: > 0 }))
        {
            context.Logger.LogWarning("plan.confirm stripped dependsOn from {Count} revised subtask(s) — this is a flat plan for a parallel fan-out", plan.Subtasks.Count(t => t.DependsOn is { Count: > 0 }));

            plan = plan with { Subtasks = plan.Subtasks.Select(t => t with { DependsOn = null }).ToList() };
        }

        var items = plan.Subtasks.Select(WorkPlanItem.From).ToList();

        if (WorkPlanItemGraph.Validate(items) is { } graphError) return NodeResult.Fail($"The revised plan is structurally invalid: {graphError}");

        await plans.SetStatusAsync(current.Id, teamId, WorkPlanStatuses.AwaitingConfirmation, WorkPlanStatuses.Rejected, cancellationToken).ConfigureAwait(false);

        var revised = await plans.SaveVersionAsync(new WorkPlanDraft
        {
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            OriginKind = WorkPlanOrigins.Confirm,
            OriginKey = $"{RevisionKeyPrefix}{current.Version}",
            Goal = plan.Goal,
            Items = items,
            SuccessCriteria = plan.SuccessCriteria,
            Risks = plan.Risks,
            Assumptions = plan.Assumptions,
            Questions = plan.Questions,
        }, cancellationToken).ConfigureAwait(false);

        context.Logger.LogInformation("plan.confirm revised plan v{Old} → v{New} for run {RunId} against the operator's feedback", current.Version, revised.Version, workflowRunId);

        return await ParkAsync(plans, revised, teamId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Park on THIS version's Action wait: flip it AwaitingConfirmation (CAS — a replay no-ops) and suspend with the discriminator payload the confirm endpoint + UI match on. The engine keeps one wait per (node, iteration), so a crash re-park on the same version is deduped.</summary>
    private static async Task<NodeResult> ParkAsync(IWorkPlanService plans, WorkPlan version, Guid teamId, CancellationToken cancellationToken)
    {
        await plans.SetStatusAsync(version.Id, teamId, WorkPlanStatuses.Authored, WorkPlanStatuses.AwaitingConfirmation, cancellationToken).ConfigureAwait(false);

        var payload = JsonSerializer.SerializeToElement(new { kind = WaitPayloadKind, planId = version.Id, version = version.Version });

        return NodeResult.Suspend(new SuspensionToken { Kind = WorkflowWaitKinds.Action, Payload = payload, IterationKey = IterationKeyFor(version.Version) });
    }

    /// <summary>The per-version wait identity — a revision parks on a FRESH wait while the old version's resolved wait stays as history.</summary>
    public static string IterationKeyFor(int version) => $"plan-confirm#v{version}";

    /// <summary>The revision origin-key prefix — <c>plan-confirm#rev-of-v{N}</c> is exactly-once per rejected version, the crash-replay dedupe.</summary>
    public const string RevisionKeyPrefix = "plan-confirm#rev-of-v";

    /// <summary>How many revisions the chain already took: a revision's key <c>rev-of-v{N}</c> means N prior versions existed, i.e. N revisions happened by the time it was authored (v2 = rev-of-v1 = the 1st revision). The planner's original reads 0.</summary>
    private static int RevisionDepth(WorkPlan current)
    {
        if (current.OriginKey is { } key && key.StartsWith(RevisionKeyPrefix, StringComparison.Ordinal) && int.TryParse(key.AsSpan(RevisionKeyPrefix.Length), out var rejectedVersion))
            return rejectedVersion;

        return 0;
    }

    /// <summary>
    /// Crash-recovery fallback: the answer survives on THIS version's RESOLVED wait row even when the re-dispatch
    /// lost the resume payload — the same fold the supervisor's ask recovery uses. Null while the wait is still
    /// Pending (⇒ re-park) or absent.
    /// </summary>
    private static async Task<string?> ReadResolvedWaitAnswerAsync(CodeSpaceDbContext db, NodeRunContext context, Guid workflowRunId, int version, CancellationToken cancellationToken)
    {
        var payloadJson = await db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == workflowRunId && w.NodeId == context.NodeId && w.IterationKey == IterationKeyFor(version) && w.WaitKind == WorkflowWaitKinds.Action && w.Status == WorkflowWaitStatuses.Resolved)
            .Select(w => w.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (payloadJson == null) return null;

        try
        {
            var root = JsonDocument.Parse(payloadJson).RootElement;

            var comment = root.TryGetProperty("comment", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;

            if (!string.IsNullOrWhiteSpace(comment)) return comment;

            return root.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(a.GetString()) ? a.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The same fail-closed release predicate as the supervisor gate — anything that does not lead with the approve word is revision feedback.</summary>
    private static bool Approves(string answer) =>
        answer.TrimStart().StartsWith(SupervisorApprovalRequest.ApproveReply, StringComparison.OrdinalIgnoreCase);

    /// <summary>The approved plan's outputs — items verbatim from the durable contract; <c>json.subtasks</c> mirrors plan.author's binding shape so a flow.map binds this node identically.</summary>
    private static Dictionary<string, JsonElement> BuildOutputs(WorkPlan approved)
    {
        var items = JsonDocument.Parse(approved.ItemsJson).RootElement.Clone();

        return new Dictionary<string, JsonElement>
        {
            ["planId"] = JsonSerializer.SerializeToElement(approved.Id),
            ["version"] = JsonSerializer.SerializeToElement(approved.Version),
            ["approved"] = JsonSerializer.SerializeToElement(true),
            ["goal"] = JsonSerializer.SerializeToElement(approved.Goal),
            ["items"] = items,
            ["json"] = JsonSerializer.SerializeToElement(new { subtasks = items }, AgentJson.Options),
        };
    }

    private static int ReadMaxRevisions(IReadOnlyDictionary<string, JsonElement> config) =>
        config.TryGetValue("maxRevisions", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) && n >= 1 ? n : DefaultMaxRevisions;

    private static bool ReadFlat(IReadOnlyDictionary<string, JsonElement> config) =>
        config.TryGetValue("flatPlan", out var flat) && flat.ValueKind == JsonValueKind.True;

    private static string ReadString(IReadOnlyDictionary<string, JsonElement> bag, string key) =>
        bag.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
}

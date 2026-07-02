using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Plans;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Plans;

/// <summary>
/// The tape-deriving <see cref="IWorkPlanChecklistService"/>. The contract half is the persisted
/// <c>work_plan</c> row verbatim; the execution half is derived per producer:
/// <list type="bullet">
///   <item>SUPERVISOR plans — walk the run's decision ledger in Sequence order: every spawn/retry decision's
///         plan-local subtask ids join POSITIONALLY onto its staged agent-run ids / folded agent results (the
///         same order the fold + per-unit acceptance rely on); a LATER attempt (retry) supersedes the item's
///         state; folded results carry the terminal status + acceptance verdict, and a still-in-flight
///         staging (no fold yet) reads the live <c>agent_run</c> status.</item>
///   <item>PLAN-MAP plans (plan.author / plan.confirm origins) — the fan-out stages one agent run per item
///         under the map branch key <c>map#i</c>, joined POSITIONALLY onto <c>items[i]</c> (the map fans the
///         plan's items out in order); the latest run per branch carries the live status + the S5 oracle
///         verdict. Shapes without a map fan-out keep their items honestly <c>Pending</c>.</item>
/// </list>
/// READ-ONLY: recomputed per request, never persisted.
///
/// <para>Known bounds (documented, not defended): the substrate runs ONE supervisor node per run (two would
/// interleave one decision ledger — a pre-existing engine constraint, not a checklist one), and the
/// supervisor's plan validator tolerates duplicate subtask ids (a dup renders as two lines sharing the latest
/// attempt). When a third producer arrives, per-origin derivation should become a Rule-18.3 sources folder
/// instead of growing an if/else here.</para>
/// </summary>
public sealed class WorkPlanChecklistService : IWorkPlanChecklistService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IWorkPlanService _plans;

    public WorkPlanChecklistService(CodeSpaceDbContext db, IWorkPlanService plans)
    {
        _db = db;
        _plans = plans;
    }

    public async Task<WorkPlanChecklist?> GetCurrentAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken)
    {
        var plan = await _plans.GetCurrentAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false);

        if (plan == null) return null;

        var items = Deserialize<List<WorkPlanItem>>(plan.ItemsJson) ?? new List<WorkPlanItem>();

        var attempts = plan.OriginKind == WorkPlanOrigins.Supervisor
            ? await DeriveSupervisorAttemptsAsync(workflowRunId, teamId, cancellationToken).ConfigureAwait(false)
            : await DerivePlanMapAttemptsAsync(workflowRunId, teamId, items, cancellationToken).ConfigureAwait(false);

        return new WorkPlanChecklist
        {
            PlanId = plan.Id,
            WorkflowRunId = plan.WorkflowRunId,
            Version = plan.Version,
            Status = plan.Status,
            OriginKind = plan.OriginKind,
            Goal = plan.Goal,
            SuccessCriteria = Deserialize<List<string>>(plan.SuccessCriteriaJson),
            Risks = Deserialize<List<string>>(plan.RisksJson),
            Assumptions = Deserialize<List<string>>(plan.AssumptionsJson),
            Questions = Deserialize<List<WorkPlanQuestion>>(plan.QuestionsJson),
            Items = items.Select(item => ChecklistItem(item, attempts)).ToList(),
        };
    }

    private static WorkPlanChecklistItem ChecklistItem(WorkPlanItem item, IReadOnlyDictionary<string, WorkPlanItemAttempt> attempts)
    {
        var attempt = attempts.TryGetValue(item.Id, out var a) ? a : null;

        return new WorkPlanChecklistItem
        {
            Item = item,
            State = WorkPlanItemStates.Derive(attempt?.LatestStatus, attempt?.AcceptancePassed),
            AgentRunId = attempt?.AgentRunId,
            AcceptancePassed = attempt?.AcceptancePassed,
            AcceptanceDetail = attempt?.AcceptanceDetail,
            Attempts = attempt?.Count ?? 0,
        };
    }

    /// <summary>
    /// The PLAN-MAP tiers' execution linkage (S5): the fan-out stages one agent run per plan item under the map
    /// branch iteration key <c>map#i</c>, and the map fans out the plan's items IN ORDER — so <c>map#i ↔ items[i]</c>
    /// is the same positional contract the supervisor fold rides. The latest run per branch index (a branch
    /// rerun supersedes) carries the live status + the S5 oracle verdict off its folded result. Runs whose
    /// iteration key is not a map branch (a single-agent graph, a nested shape) contribute nothing — those
    /// items stay honestly Pending.
    /// </summary>
    private async Task<Dictionary<string, WorkPlanItemAttempt>> DerivePlanMapAttemptsAsync(Guid runId, Guid teamId, IReadOnlyList<WorkPlanItem> items, CancellationToken cancellationToken)
    {
        var attempts = new Dictionary<string, WorkPlanItemAttempt>(StringComparer.Ordinal);

        if (items.Count == 0) return attempts;

        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && r.TeamId == teamId && r.IterationKey.StartsWith("map#"))
            .OrderBy(r => r.CreatedDate)
            .Select(r => new { r.Id, r.IterationKey, r.Status, r.ResultJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var row in rows)
        {
            if (!TryParseBranchIndex(row.IterationKey, out var index) || index >= items.Count) continue;

            var itemId = items[index].Id;
            var (passed, detail) = ReadAcceptanceVerdict(row.ResultJson);
            var prior = attempts.TryGetValue(itemId, out var p) ? p : null;

            attempts[itemId] = new WorkPlanItemAttempt(row.Id, row.Status.ToString(), passed, detail, (prior?.Count ?? 0) + 1);
        }

        return attempts;
    }

    /// <summary>The branch ordinal out of a <c>map#i</c> iteration key (top-level fan-outs only — a combined/nested key does not parse and is skipped).</summary>
    private static bool TryParseBranchIndex(string iterationKey, out int index) =>
        int.TryParse(iterationKey.AsSpan("map#".Length), out index) && index >= 0;

    /// <summary>The S5 oracle verdict off the run's folded result — null/null when the task carried no contract (or the run has no result yet).</summary>
    private static (bool? Passed, string? Detail) ReadAcceptanceVerdict(string? resultJson)
    {
        if (string.IsNullOrEmpty(resultJson)) return (null, null);

        try
        {
            var root = JsonDocument.Parse(resultJson).RootElement;

            var passed = root.TryGetProperty("acceptancePassed", out var ap) && ap.ValueKind is JsonValueKind.True or JsonValueKind.False ? ap.GetBoolean() : (bool?)null;
            var detail = root.TryGetProperty("acceptanceDetail", out var ad) && ad.ValueKind == JsonValueKind.String ? ad.GetString() : null;

            return (passed, detail);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Fold the ledger's spawn/retry decisions into a latest-attempt view per plan-local item id. The
    /// positional subtaskIds[i] ↔ staged/folded[i] join is the SAME contract the supervisor's result fold and
    /// per-unit acceptance gate rely on, so this projection can never disagree with what the decider saw.
    /// </summary>
    private async Task<Dictionary<string, WorkPlanItemAttempt>> DeriveSupervisorAttemptsAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && (d.DecisionKind == SupervisorDecisionKinds.Spawn || d.DecisionKind == SupervisorDecisionKinds.Retry))
            .OrderBy(d => d.Sequence)
            .Select(d => new { d.DecisionKind, d.PayloadJson, d.OutcomeJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var decisions = rows.Select(d => new SupervisorTapeDecision(d.DecisionKind, d.PayloadJson, d.OutcomeJson)).ToList();
        var liveStatuses = await LoadLiveStatusesAsync(decisions, teamId, cancellationToken).ConfigureAwait(false);

        return FoldAttempts(decisions, liveStatuses);
    }

    /// <summary>
    /// The PURE latest-attempt fold (internal so the unit tier pins retry-supersede / failed-staging /
    /// folded-beats-live without a database): walk the spawn/retry decisions in Sequence order, join each
    /// decision's plan-local subtask ids POSITIONALLY onto its staged/folded agents, and let a later attempt
    /// supersede the item's state while the attempt count accumulates.
    /// </summary>
    internal static Dictionary<string, WorkPlanItemAttempt> FoldAttempts(IReadOnlyList<SupervisorTapeDecision> decisions, IReadOnlyDictionary<Guid, string> liveStatuses)
    {
        var attempts = new Dictionary<string, WorkPlanItemAttempt>(StringComparer.Ordinal);

        foreach (var decision in decisions)
        {
            var subtaskIds = SubtaskIdsOf(decision.Kind, decision.PayloadJson);
            var staged = SupervisorOutcome.ReadStagedAgentRunIds(decision.OutcomeJson);
            var folded = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson);

            for (var i = 0; i < subtaskIds.Count && i < staged.Count; i++)
            {
                var folds = i < folded.Count ? folded[i] : null;
                var status = folds?.Status ?? (liveStatuses.TryGetValue(staged[i], out var live) ? live : null);

                var prior = attempts.TryGetValue(subtaskIds[i], out var existing) ? existing.Count : 0;
                attempts[subtaskIds[i]] = new WorkPlanItemAttempt(staged[i], status, folds?.AcceptancePassed, folds?.AcceptanceDetail, prior + 1);
            }
        }

        return attempts;
    }

    /// <summary>
    /// Live agent-run statuses for the staged ids of decisions whose results are NOT yet folded (an in-flight
    /// wave) — settled waves read their folded status, so their agents are skipped. One batched read,
    /// team-filtered as defense-in-depth (the ids already come from the run's own team-scoped tape).
    /// </summary>
    private async Task<Dictionary<Guid, string>> LoadLiveStatusesAsync(IReadOnlyList<SupervisorTapeDecision> decisions, Guid teamId, CancellationToken cancellationToken)
    {
        var ids = decisions
            .Where(d => SupervisorOutcome.ReadAgentResults(d.OutcomeJson).Count == 0)
            .SelectMany(d => SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson))
            .Distinct().ToList();

        if (ids.Count == 0) return new Dictionary<Guid, string>();

        // Enum→string client-side (ToString is not SQL-translatable); the id list is bounded by the run's own spawn caps.
        var rows = await _db.AgentRun.AsNoTracking()
            .Where(r => ids.Contains(r.Id) && r.TeamId == teamId)
            .Select(r => new { r.Id, r.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.ToDictionary(r => r.Id, r => r.Status.ToString());
    }

    /// <summary>The plan-local item ids a spawn/retry decision targeted, in staging order (spawn = the list; retry = the single id).</summary>
    private static IReadOnlyList<string> SubtaskIdsOf(string kind, string payloadJson)
    {
        if (kind == SupervisorDecisionKinds.Retry)
        {
            var retry = Deserialize<SupervisorRetryPayload>(payloadJson);
            return retry == null ? Array.Empty<string>() : new[] { retry.SubtaskId };
        }

        var spawn = Deserialize<SupervisorSpawnPayload>(payloadJson);
        return spawn?.SubtaskIds ?? Array.Empty<string>();
    }

    private static T? Deserialize<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        try { return JsonSerializer.Deserialize<T>(json, AgentJson.Options); }
        catch (JsonException) { return null; }
    }

}

/// <summary>The latest attempt view for one item id: who ran it last, its status, its acceptance verdict, and how many attempts it has had. Internal — the unit tier pins the fold through it.</summary>
internal sealed record WorkPlanItemAttempt(Guid AgentRunId, string? LatestStatus, bool? AcceptancePassed, string? AcceptanceDetail, int Count);

/// <summary>One spawn/retry decision's tape view (kind + frozen payload + recorded outcome) — the fold's input row.</summary>
internal sealed record SupervisorTapeDecision(string Kind, string PayloadJson, string? OutcomeJson);

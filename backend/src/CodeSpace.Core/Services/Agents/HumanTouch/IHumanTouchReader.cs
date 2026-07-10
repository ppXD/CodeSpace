using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Agents.HumanTouch;

/// <summary>
/// The bulk "did this run ever stop to ask a human anything" read — joining the FIVE durable places a human touch
/// can be recorded: an <c>ask_human</c> <see cref="Supervisor.SupervisorDecisionKinds.AskHuman"/>
/// <c>SupervisorDecisionRecord</c> (agent-grain, keyed directly on <c>SupervisorRunId</c>, which IS the
/// <c>WorkflowRunId</c>); an approval-parked MCP tool call — either a plain side-effecting call or a
/// <c>decision.request</c> row — in <c>ToolCallLedger</c> (agent-grain, joined through <c>AgentRun.WorkflowRunId</c>
/// since the ledger has no <c>WorkflowRunId</c> of its own); a <c>flow.wait_approval</c> / <c>flow.decision</c>
/// node's <c>WorkflowRunWait</c> row (node-grain, joined through <c>WorkflowRun.TeamId</c> for tenancy); and a
/// human-initiated pull-request open recorded on <c>PublishManifest</c> (<see cref="RoomOpenedPullRequestCountsAsync"/>).
///
/// <para>Every kind is filtered to a GENUINE human touch, never merely "a park occurred": an <c>ask_human</c>
/// decision that was REJECTED (blank question) or DEGRADED (no usable conversation) self-advances without ever
/// posting a card — <see cref="SupervisorOutcome.ReadHumanWaitToken"/> is null for both, so neither counts. The
/// Decision substrate (D2-D4) answers a parked call/wait with a <see cref="DecisionAnswer"/> whose
/// <see cref="DecisionAnswer.AnsweredBy"/> may be <see cref="DecisionAnsweredByKinds.Human"/>, but may equally be
/// <see cref="DecisionAnsweredByKinds.Supervisor"/> (a confident arbiter auto-answer) or
/// <see cref="DecisionAnsweredByKinds.Timeout"/> (the bounded-wait deadline's default) — NEITHER of which involved a
/// person, so only <c>AnsweredBy == Human</c> counts. A plain (non-decision) approval's timeout-reaper
/// <c>Expired</c> resolution is excluded the same way (nobody ever clicked); <c>flow.wait_approval</c> has no
/// auto-resolution path at all, so any RESOLVED row there is unconditionally a human action.
///
/// <para>Team-scoped, read-only, bulk-by-design (mirrors <c>IPublishManifestStore.ListForWorkflowRunsAsync</c>) —
/// the unattended-delivery scorecard needs "touch count for N runs" in one pass, never one query per run. A run
/// absent from the result had zero touches.</para>
/// </summary>
public interface IHumanTouchReader
{
    Task<IReadOnlyDictionary<Guid, int>> CountByWorkflowRunAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken);
}

public sealed class HumanTouchReader : IHumanTouchReader, IScopedDependency
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly CodeSpaceDbContext _db;

    public HumanTouchReader(CodeSpaceDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyDictionary<Guid, int>> CountByWorkflowRunAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (workflowRunIds.Count == 0) return Empty;

        var askHumanCounts = await AskHumanCountsAsync(workflowRunIds, teamId, cancellationToken).ConfigureAwait(false);
        var approvalCounts = await ApprovalCountsAsync(workflowRunIds, teamId, cancellationToken).ConfigureAwait(false);
        var nodeWaitCounts = await NodeWaitCountsAsync(workflowRunIds, teamId, cancellationToken).ConfigureAwait(false);
        var pullRequestCounts = await RoomOpenedPullRequestCountsAsync(workflowRunIds, teamId, cancellationToken).ConfigureAwait(false);

        return workflowRunIds
            .Select(id => (id, count: askHumanCounts.GetValueOrDefault(id) + approvalCounts.GetValueOrDefault(id) + nodeWaitCounts.GetValueOrDefault(id) + pullRequestCounts.GetValueOrDefault(id)))
            .Where(x => x.count > 0)
            .ToDictionary(x => x.id, x => x.count);
    }

    /// <summary>ask_human decisions that actually parked on a human — excludes a REJECTED (blank question) or DEGRADED (no usable conversation) self-advance, neither of which ever posted a card. SupervisorRunId IS the WorkflowRunId — no join needed.</summary>
    private async Task<IReadOnlyDictionary<Guid, int>> AskHumanCountsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.TeamId == teamId && workflowRunIds.Contains(d.SupervisorRunId) && d.DecisionKind == SupervisorDecisionKinds.AskHuman)
            .Select(d => new { d.SupervisorRunId, d.OutcomeJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .Where(r => SupervisorOutcome.ReadHumanWaitToken(r.OutcomeJson) is not null)
            .GroupBy(r => r.SupervisorRunId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// Approval-parked agent-grain tool calls resolved by a GENUINE human — a plain side-effecting approval resolves
    /// only via an explicit human Approve/Reject click (its one non-human exit, a timeout-reaper <c>Expired</c>, is
    /// excluded); a <c>decision.request</c> row may instead be answered by the Decision substrate's supervisor
    /// arbiter or bounded-wait deadline (<see cref="IsHumanAnswered"/> excludes both). Joined through
    /// <c>AgentRun.WorkflowRunId</c> since the ledger has no <c>WorkflowRunId</c> of its own.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, int>> ApprovalCountsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await (
                from t in _db.ToolCallLedger.AsNoTracking()
                join a in _db.AgentRun.AsNoTracking() on t.AgentRunId equals a.Id
                where t.TeamId == teamId && t.ApprovalToken != null && a.WorkflowRunId != null && workflowRunIds.Contains(a.WorkflowRunId.Value)
                select new { WorkflowRunId = a.WorkflowRunId!.Value, t.ToolKind, t.Status, t.ResultJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .Where(r => IsGenuineHumanApproval(r.ToolKind, r.Status, r.ResultJson))
            .GroupBy(r => r.WorkflowRunId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// <c>flow.wait_approval</c> / <c>flow.decision</c> node-grain waits resolved by a GENUINE human — the node-grain
    /// sibling of <see cref="ApprovalCountsAsync"/> (the same Decision substrate answers both grains identically).
    /// <c>flow.wait_approval</c> (<see cref="WorkflowWaitKinds.Approval"/>) has no auto-resolution path at all, so any
    /// RESOLVED row there is unconditionally a human action; <c>flow.decision</c>
    /// (<see cref="WorkflowWaitKinds.Decision"/>) may instead be answered by the arbiter/deadline, so
    /// <see cref="IsHumanAnswered"/> applies the same exclusion. Joined through <c>WorkflowRun</c> for tenancy
    /// (<c>WorkflowRunWait</c> carries no <c>TeamId</c> of its own).
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, int>> NodeWaitCountsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await (
                from w in _db.WorkflowRunWait.AsNoTracking()
                join r in _db.WorkflowRun.AsNoTracking() on w.RunId equals r.Id
                where r.TeamId == teamId && workflowRunIds.Contains(w.RunId) && (w.WaitKind == WorkflowWaitKinds.Approval || w.WaitKind == WorkflowWaitKinds.Decision)
                select new { w.RunId, w.WaitKind, w.Status, w.PayloadJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .Where(r => IsGenuineHumanNodeWait(r.WaitKind, r.Status, r.PayloadJson))
            .GroupBy(r => r.RunId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>
    /// A pull request opened through the Room's "Open PR" action (<c>RoomPullRequestService.OpenAsync</c>) — the
    /// FIFTH durable touch, and the one the M-1 sweep left uncounted: an operator clicking that button is a genuine
    /// human touch, but it posts no <c>ask_human</c> row, no approval, and no node wait, so it was invisible to the
    /// other three sources. Recorded on <see cref="PublishManifest"/> (<see cref="PublishManifestKind.Integration"/>,
    /// <see cref="PublishManifest.PullRequestNumber"/> set) — <c>CreatedBy</c> is the discriminator, NOT the mere
    /// presence of a PR number: EF's audit stamping (<c>CodeSpaceDbContext.ApplyAuditFields</c>) writes the real
    /// authenticated <c>ICurrentUser</c> for a Room-initiated, HTTP-request-scoped call, and
    /// <see cref="SystemUsers.SeederId"/> (<c>BackgroundSeederUser</c>) for anything running outside an HTTP
    /// context — a background job, a recurring job, or a future server-authored deliver-at-stop PR-open. This
    /// keys on WHO opened it, not on whether a PR exists, so a server-side open is correctly never counted here.
    /// </summary>
    private async Task<IReadOnlyDictionary<Guid, int>> RoomOpenedPullRequestCountsAsync(IReadOnlyCollection<Guid> workflowRunIds, Guid teamId, CancellationToken cancellationToken)
    {
        var rows = await _db.PublishManifest.AsNoTracking()
            .Where(m => m.TeamId == teamId
                && m.WorkflowRunId != null && workflowRunIds.Contains(m.WorkflowRunId.Value)
                && m.Kind == PublishManifestKind.Integration
                && m.PullRequestNumber != null
                && m.CreatedBy != SystemUsers.SeederId)
            .Select(m => m.WorkflowRunId!.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows
            .GroupBy(id => id)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    /// <summary>A plain approval resolves ONLY via an explicit human click (its one non-human exit, the timeout reaper's <see cref="ToolCallLedgerStatus.Expired"/>, is excluded up front); a <c>decision.request</c> row instead defers to <see cref="IsHumanAnswered"/> since it may auto-resolve via the arbiter/deadline. Left non-decision, non-Expired rows always count.</summary>
    private static bool IsGenuineHumanApproval(string toolKind, ToolCallLedgerStatus status, string? resultJson)
    {
        if (status == ToolCallLedgerStatus.Expired) return false;

        return toolKind != DecisionToolKinds.DecisionRequest || IsHumanAnswered(resultJson);
    }

    /// <summary><c>flow.wait_approval</c> is unconditionally human once resolved (no auto path exists); a still-Pending row (not expected on a terminal run, but handled fail-safe as counted) and <c>flow.decision</c> defer to <see cref="IsHumanAnswered"/>.</summary>
    private static bool IsGenuineHumanNodeWait(string waitKind, string status, string? payloadJson)
    {
        if (status != WorkflowWaitStatuses.Resolved) return true;

        return waitKind == WorkflowWaitKinds.Approval || IsHumanAnswered(payloadJson);
    }

    /// <summary>True when the stashed <see cref="DecisionAnswer"/> was answered by an actual person (<see cref="DecisionAnsweredByKinds.Human"/>) rather than the supervisor arbiter or the bounded-wait timeout default. A missing/unparseable answer defaults to counted — fail-safe toward "attended" rather than silently overstating unattended.</summary>
    private static bool IsHumanAnswered(string? answerJson)
    {
        if (string.IsNullOrWhiteSpace(answerJson)) return true;

        try
        {
            var answer = JsonSerializer.Deserialize<DecisionAnswer>(answerJson, Json);
            return answer is null || answer.AnsweredBy == DecisionAnsweredByKinds.Human;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    private static readonly IReadOnlyDictionary<Guid, int> Empty = new Dictionary<Guid, int>();
}

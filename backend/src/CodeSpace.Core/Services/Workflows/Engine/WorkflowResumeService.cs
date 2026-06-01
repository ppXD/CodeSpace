using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Wakes a suspended run. The single entry point for every resume signal — a scheduled timer
/// (engine schedules <see cref="ResumeAsync"/> at wake_at), a human approval, or an external
/// callback (Phase 1.2 call this same method). Resolves the run's outstanding waits, flips the
/// run Suspended -> Pending, and re-dispatches; the durable walker rehydrates, injects the
/// resolved payload as the suspended node's ResumePayload, and continues.
/// </summary>
public interface IWorkflowResumeService
{
    /// <summary>
    /// Resume <paramref name="runId"/> if it is Suspended. Returns false (no-op) when the run
    /// isn't Suspended — already resumed, terminal, or never suspended — so duplicate signals
    /// (timer + manual, retries) are idempotent.
    /// </summary>
    Task<bool> ResumeAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Resume with an explicit payload (the approver's decision, the callback body) — stamped
    /// onto the resolved wait and injected as the node's ResumePayload on re-run. Same
    /// idempotent Suspended→Pending gate as the no-payload overload.
    /// </summary>
    Task<bool> ResumeAsync(Guid runId, string resumePayloadJson, CancellationToken cancellationToken);

    /// <summary>
    /// Resume the run parked on the Callback wait matching <paramref name="token"/>, stamping the
    /// posted <paramref name="bodyJson"/> as the resume payload (surfaced as the node's <c>body</c>
    /// output). Returns false when no pending callback wait matches the token (unknown / already
    /// resolved) — the caller maps that to 404. The token is the bearer secret for the
    /// unauthenticated callback URL.
    /// </summary>
    Task<bool> ResumeByCallbackTokenAsync(string token, string bodyJson, CancellationToken cancellationToken);

    /// <summary>
    /// Resume the run parked on the Action wait matching <paramref name="token"/> — a person acted
    /// on an interactive chat affordance. Resolves ONLY that wait (not the run's other pending waits,
    /// so two parallel cards resolve independently with their own decision), stamping a structured
    /// <c>{ action, by, comment }</c> payload (surfaced as the suspended node's outputs). Scoped to
    /// <paramref name="teamId"/>: the wait's run must belong to that team, or it no-ops (tenancy guard).
    /// <paramref name="actorUserId"/> is the authenticated clicker. Returns false when no pending Action
    /// wait matches the token in that team (unknown / already resolved / cross-team) — caller maps to 404/409.
    /// </summary>
    Task<bool> ResumeByActionTokenAsync(string token, string actionKey, Guid actorUserId, string? comment, Guid teamId, CancellationToken cancellationToken);
}

public sealed class WorkflowResumeService : IWorkflowResumeService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowRunDispatcher _dispatcher;
    private readonly ILogger<WorkflowResumeService> _logger;

    public WorkflowResumeService(CodeSpaceDbContext db, IWorkflowRunDispatcher dispatcher, ILogger<WorkflowResumeService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public Task<bool> ResumeAsync(Guid runId, CancellationToken cancellationToken) =>
        ResumeCoreAsync(runId, resumePayloadJson: null, onlyWaitId: null, cancellationToken);

    public Task<bool> ResumeAsync(Guid runId, string resumePayloadJson, CancellationToken cancellationToken) =>
        ResumeCoreAsync(runId, resumePayloadJson, onlyWaitId: null, cancellationToken);

    // onlyWaitId: when set, resolve ONLY that wait (a token-keyed resume targets one specific
    // affordance — so a run with several parallel waits resolves each independently with its own
    // payload). When null, resolve every pending wait for the run (a run-level wake: timer / approval).
    private async Task<bool> ResumeCoreAsync(Guid runId, string? resumePayloadJson, Guid? onlyWaitId, CancellationToken cancellationToken)
    {
        // Single-writer gate: only one resume flips Suspended -> Pending. Concurrent signals
        // (a fired timer + a manual resume, or a Hangfire retry of the timer job) cannot both
        // proceed — the loser's UPDATE affects 0 rows and it no-ops.
        var flipped = await _db.WorkflowRun
            .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Suspended)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Pending), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0)
        {
            _logger.LogDebug("Resume: run {RunId} not Suspended — skipping (already resumed / terminal / never suspended)", runId);
            return false;
        }

        // Resolve every pending wait for the run + stamp the resume payload (the durable walker
        // injects payload_jsonb as the node's ResumePayload on re-run). A timer wake supplies no
        // payload, so we stamp a default marker; approval / callback supply their decision body.
        var now = DateTimeOffset.UtcNow;
        var payload = resumePayloadJson ?? JsonSerializer.Serialize(new { resumed_at = now.ToString("o") });
        await _db.WorkflowRunWait
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && (onlyWaitId == null || w.Id == onlyWaitId))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkflowWaitStatuses.Resolved)
                .SetProperty(w => w.PayloadJson, payload)
                .SetProperty(w => w.ResolvedAt, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);

        // Re-dispatch via the existing Pending -> Enqueued -> engine path. The walker rehydrates
        // and continues from the suspended node.
        await _dispatcher.DispatchAsync(runId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Resume: run {RunId} resumed (Suspended -> Pending -> dispatched)", runId);
        return true;
    }

    public async Task<bool> ResumeByCallbackTokenAsync(string token, string bodyJson, CancellationToken cancellationToken)
    {
        var wait = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.Token == token && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.Callback)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (wait == null)
        {
            _logger.LogDebug("Callback resume: no pending callback wait matches the token");
            return false;
        }

        return await ResumeCoreAsync(wait.RunId, bodyJson, onlyWaitId: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ResumeByActionTokenAsync(string token, string actionKey, Guid actorUserId, string? comment, Guid teamId, CancellationToken cancellationToken)
    {
        // The wait's run must belong to the caller's team — a card can only resolve a wait in its own
        // tenant, even though the token is an unguessable Guid (defense in depth against a cross-team
        // card / a leaked token). The join keeps this a single indexed query.
        var wait = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.Token == token && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.Action)
            .Where(w => _db.WorkflowRun.Any(r => r.Id == w.RunId && r.TeamId == teamId))
            .Select(w => new { w.Id, w.RunId })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (wait == null)
        {
            _logger.LogDebug("Action resume: no pending action wait matches the token in team {TeamId}", teamId);
            return false;
        }

        var payload = JsonSerializer.Serialize(new { action = actionKey, by = actorUserId, comment });

        // Resolve ONLY this wait — a sibling card parked on the same run keeps waiting for its own click.
        return await ResumeCoreAsync(wait.RunId, payload, onlyWaitId: wait.Id, cancellationToken).ConfigureAwait(false);
    }
}

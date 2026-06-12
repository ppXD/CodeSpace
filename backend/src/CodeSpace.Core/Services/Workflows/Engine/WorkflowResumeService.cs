using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Wakes a suspended run. The single entry point for every resume signal — a scheduled timer
/// (engine schedules <see cref="ResumeWaitAsync"/> at wake_at), a human approval, or an external
/// callback. Every signal targets ONE specific wait (its own timer / approval / callback / action),
/// resolves only that wait with its payload, flips the run Suspended -> Pending, and re-dispatches;
/// the durable walker rehydrates, injects the resolved payload as the suspended node's ResumePayload,
/// and continues. A run parked on several parallel waits resolves each independently — no signal
/// collapses a sibling wait into the wrong decision.
/// </summary>
public interface IWorkflowResumeService
{
    /// <summary>
    /// Resume <paramref name="runId"/> by resolving ONLY wait <paramref name="waitId"/>, stamping
    /// <paramref name="resumePayloadJson"/> (null for a bare timer wake → a generated resumed_at
    /// marker; an approver's decision body otherwise) as that wait's payload, injected as the node's
    /// ResumePayload on re-run. Resolves nothing else, so a run parked on several parallel waits
    /// advances only the resolved branch and re-suspends on any sibling still pending. Returns false
    /// (no-op) when the run isn't Suspended — already resumed, terminal, or never suspended — so
    /// duplicate signals (a fired timer + a manual approval, a Hangfire retry) are idempotent.
    /// </summary>
    Task<bool> ResumeWaitAsync(Guid runId, Guid waitId, string? resumePayloadJson, CancellationToken cancellationToken);

    /// <summary>
    /// Resume a run on the completion of ONE external unit of work it parked on (an agent run, a
    /// sub-workflow child). Like every resume signal this resolves ONLY <paramref name="waitId"/>, but
    /// unlike <see cref="ResumeWaitAsync"/> it does so UNCONDITIONALLY (the work has durably finished, so
    /// its result MUST be consumed even if a sibling completion is mid-flight; dropping it on a run-flip
    /// CAS race is exactly the bug that fed every parallel node the first completer's payload) and
    /// idempotently (a replay / double-notify is a no-op) — then re-dispatches the run only once NO
    /// pending waits remain. So K parallel waits on one run each resolve with their OWN payload and the
    /// run advances exactly once, after the last completes. Returns false when the wait was already
    /// resolved (replay / double-notify).
    /// </summary>
    Task<bool> ResumeOnWaitCompletionAsync(Guid runId, Guid waitId, string resumePayloadJson, CancellationToken cancellationToken);

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
    /// <c>{ action, by, comment, values? }</c> payload (surfaced as the suspended node's outputs).
    /// <paramref name="values"/> carries a form submission's field values (null for a button click).
    /// Scoped to <paramref name="teamId"/>: the wait's run must belong to that team (tenancy guard).
    /// <paramref name="actorUserId"/> is the authenticated responder. Returns an <see cref="ActionResumeResult"/>
    /// so the caller can tell a decision it should RECORD (<see cref="ActionResumeResult.Resumed"/> /
    /// <see cref="ActionResumeResult.NoWait"/>) from one it must REJECT
    /// (<see cref="ActionResumeResult.AlreadyResolved"/> — a deadline or another responder already decided).
    /// </summary>
    Task<ActionResumeResult> ResumeByActionTokenAsync(string token, string actionKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Resume the run parked on wait <paramref name="waitId"/> because its DEADLINE passed with no
    /// response — stamping <paramref name="timeoutPayloadJson"/> (the node's default-on-timeout decision)
    /// as the resumed node's payload. Resolves ONLY that wait. No-ops when the wait is no longer Pending
    /// (a human resolved it first), so the scheduled deadline job and a human click are mutually
    /// idempotent (whoever flips the wait first wins; the other is a no-op). Invoked by the scheduled
    /// background job the engine enqueues for a bounded wait.
    /// </summary>
    Task<bool> ResumeByDeadlineAsync(Guid waitId, string timeoutPayloadJson, CancellationToken cancellationToken);
}

public sealed class WorkflowResumeService : IWorkflowResumeService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IWorkflowRunDispatcher _dispatcher;
    private readonly IActorIdentityRequirementGate _identityGate;
    private readonly IPostCommitActions _postCommit;
    private readonly ILogger<WorkflowResumeService> _logger;

    public WorkflowResumeService(CodeSpaceDbContext db, IWorkflowRunDispatcher dispatcher, IActorIdentityRequirementGate identityGate, IPostCommitActions postCommit, ILogger<WorkflowResumeService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _identityGate = identityGate;
        _postCommit = postCommit;
        _logger = logger;
    }

    public Task<bool> ResumeWaitAsync(Guid runId, Guid waitId, string? resumePayloadJson, CancellationToken cancellationToken) =>
        ResumeCoreAsync(runId, resumePayloadJson, waitId, cancellationToken);

    // Resolve ONLY waitId — every resume signal targets one specific wait (its timer / approval /
    // callback / action), so a run parked on several parallel waits resolves each independently with
    // its own payload. The run flips Suspended -> Pending and re-dispatches; the walker advances the
    // resolved branch and re-suspends on any sibling wait still pending.
    private async Task<bool> ResumeCoreAsync(Guid runId, string? resumePayloadJson, Guid waitId, CancellationToken cancellationToken)
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

        // Resolve the ONE targeted wait + stamp the resume payload (the durable walker injects
        // payload_jsonb as the node's ResumePayload on re-run). A bare timer wake supplies no payload,
        // so we stamp a default marker; approval / callback / action supply their decision body. The
        // status guard keeps this idempotent against a deadline / sibling resume that already resolved it.
        var now = DateTimeOffset.UtcNow;
        var payload = resumePayloadJson ?? JsonSerializer.Serialize(new { resumed_at = now.ToString("o") });
        await _db.WorkflowRunWait
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && w.Id == waitId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkflowWaitStatuses.Resolved)
                .SetProperty(w => w.PayloadJson, payload)
                .SetProperty(w => w.ResolvedAt, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);

        // Re-dispatch via the existing Pending -> Enqueued -> engine path, deferred until AFTER this
        // resume's transaction commits. RunAfterCommitAsync runs inline when there's no ambient
        // transaction (a timer / callback / deadline resume), but DEFERS when one is open (a human
        // respond folded into the RespondToMessageCommand transaction) — otherwise a worker could fetch
        // the job before the Suspended->Pending flip is visible and CAS-no-op it into a stuck wait.
        // The walker rehydrates and continues from the suspended node.
        await _postCommit.RunAfterCommitAsync(ct => _dispatcher.DispatchAsync(runId, ct), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Resume: run {RunId} resumed (Suspended -> Pending -> dispatched)", runId);
        return true;
    }

    public async Task<bool> ResumeOnWaitCompletionAsync(Guid runId, Guid waitId, string resumePayloadJson, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Durably resolve THIS wait, independent of the run's status. A finished agent / child run MUST
        //    have its result consumed; we cannot drop it on a sibling's run-flip CAS race (the corruption
        //    where one completer's payload landed on every parallel wait). Idempotent: 0 rows = the wait was
        //    already resolved (a replay / double-notify) → no-op.
        var resolved = await _db.WorkflowRunWait
            .Where(w => w.Id == waitId && w.Status == WorkflowWaitStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkflowWaitStatuses.Resolved)
                .SetProperty(w => w.PayloadJson, resumePayloadJson)
                .SetProperty(w => w.ResolvedAt, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);

        if (resolved == 0)
        {
            _logger.LogDebug("Wait-completion resume: wait {WaitId} already resolved — skipping (replay / double-notify)", waitId);
            return false;
        }

        // 2. Re-dispatch only once the LAST parallel wait resolved. While any sibling wait is still pending
        //    the run stays Suspended — so the walker never does a partial re-walk that could miss a wait
        //    resolved just after it passed the node (the stuck race). The last completer flips
        //    Suspended→Pending (CAS); a concurrent last-completer loses the flip and the winner's single
        //    re-walk consumes every resolved wait (each keyed to its own node by the walker).
        var anyPending = await _db.WorkflowRunWait.AsNoTracking()
            .AnyAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending, cancellationToken)
            .ConfigureAwait(false);

        if (anyPending)
        {
            _logger.LogDebug("Wait-completion resume: wait {WaitId} resolved; siblings still pending on run {RunId} — staying suspended", waitId, runId);
            return true;
        }

        var flipped = await _db.WorkflowRun
            .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Suspended)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Pending), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0)
        {
            _logger.LogDebug("Wait-completion resume: run {RunId} no longer Suspended — a concurrent resume is driving the dispatch", runId);
            return true;
        }

        await _postCommit.RunAfterCommitAsync(ct => _dispatcher.DispatchAsync(runId, ct), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Wait-completion resume: run {RunId} resumed after its last parallel wait resolved", runId);
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

        // Resolve ONLY this callback's wait (located by token above). A run parked on a second
        // parallel callback / approval / action wait keeps waiting for its own signal — one POST to
        // this URL no longer collapses every pending wait into this body.
        return await ResumeCoreAsync(wait.RunId, bodyJson, wait.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ActionResumeResult> ResumeByActionTokenAsync(string token, string actionKey, Guid actorUserId, string? comment, IReadOnlyDictionary<string, JsonElement>? values, Guid teamId, CancellationToken cancellationToken)
    {
        // Look up the wait for this token REGARDLESS of status — still team-scoped (the wait's run must
        // belong to the caller's team: defense in depth against a cross-team card / leaked token). Reading
        // any status lets us tell "no wait at all" (an orphan / post-and-continue card → the caller records
        // the click) from "a wait that's no longer Pending" (a deadline or another responder already
        // decided → the caller must NOT record a divergent decision). The join keeps this a single query.
        var wait = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.Token == token && w.WaitKind == WorkflowWaitKinds.Action)
            .Where(w => _db.WorkflowRun.Any(r => r.Id == w.RunId && r.TeamId == teamId))
            .Select(w => new { w.Id, w.RunId, w.NodeId, w.Status })
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (wait == null)
        {
            _logger.LogDebug("Action resume: no action wait matches the token in team {TeamId} — wait-less card, caller records", teamId);
            return ActionResumeResult.NoWait;
        }

        if (wait.Status != WorkflowWaitStatuses.Pending)
        {
            _logger.LogDebug("Action resume: wait for token already resolved in team {TeamId} — rejecting the late click", teamId);
            return ActionResumeResult.AlreadyResolved;
        }

        // Generic act-as-user gate: if resolving this wait makes a downstream node act AS the responder on a
        // provider they haven't linked, refuse HERE — throws ActorIdentityRequiredException → 428, so the
        // client prompts a link + retries, rather than the run failing later in the background. Runs before
        // the wait is resolved, so the run stays parked for the retry.
        await _identityGate.EnsureResponderCanActAsUserAsync(wait.RunId, wait.NodeId, actorUserId, cancellationToken).ConfigureAwait(false);

        // Structured decision payload — surfaced as the suspended node's outputs. `values` (a form
        // submission) is added only when present, so a plain button click's payload is unchanged.
        var decision = new Dictionary<string, object?> { ["action"] = actionKey, ["by"] = actorUserId, ["comment"] = comment };
        if (values != null) decision["values"] = values;
        var payload = JsonSerializer.Serialize(decision);

        // Resolve ONLY this wait — a sibling card parked on the same run keeps waiting for its own click. A
        // run-flip CAS loss (a deadline / sibling resume that landed between our read and here) yields
        // AlreadyResolved, not Resumed, so the caller still won't record a divergent decision.
        var flipped = await ResumeCoreAsync(wait.RunId, payload, wait.Id, cancellationToken).ConfigureAwait(false);
        return flipped ? ActionResumeResult.Resumed : ActionResumeResult.AlreadyResolved;
    }

    public async Task<bool> ResumeByDeadlineAsync(Guid waitId, string timeoutPayloadJson, CancellationToken cancellationToken)
    {
        // Only fire if the wait is STILL pending — if a human already resolved it, this is a no-op (the
        // common case where the deadline passes after someone responded). ResumeCoreAsync's run-flip gate
        // is the final serialisation point against a click that lands in the same instant.
        var runId = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.Id == waitId && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => (Guid?)w.RunId)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (runId is null)
        {
            _logger.LogDebug("Deadline resume: wait {WaitId} is no longer pending — skipping (already resolved)", waitId);
            return false;
        }

        return await ResumeCoreAsync(runId.Value, timeoutPayloadJson, waitId, cancellationToken).ConfigureAwait(false);
    }
}

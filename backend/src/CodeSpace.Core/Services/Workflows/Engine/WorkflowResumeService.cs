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
/// RESOLVES only that wait with its payload FIRST (a status-guarded CAS on the wait — the single-writer
/// gate), THEN flips the run Suspended -> Pending and re-dispatches; the durable walker rehydrates,
/// injects the resolved payload as the suspended node's ResumePayload, and continues. Resolving the wait
/// BEFORE the run flip is what keeps two near-simultaneous signals safe — each lands its own payload on
/// its own wait, so neither is orphaned by losing a run-flip race before it resolved. A run parked on
/// several parallel waits resolves each independently — no signal collapses a sibling wait into the wrong
/// decision. The wait-COMPLETION path (<see cref="ResumeOnWaitCompletionAsync"/>) additionally holds the
/// run Suspended until the LAST sibling resolves (the wait-for-all barrier); the immediate signal paths
/// re-dispatch at once and any still-suspended sibling re-parks on the re-walk.
/// </summary>
public interface IWorkflowResumeService
{
    /// <summary>
    /// Resume <paramref name="runId"/> by resolving ONLY wait <paramref name="waitId"/>, stamping
    /// <paramref name="resumePayloadJson"/> (null for a bare timer wake → a generated resumed_at
    /// marker; an approver's decision body otherwise) as that wait's payload, injected as the node's
    /// ResumePayload on re-run. Resolves nothing else, so a run parked on several parallel waits
    /// advances only the resolved branch and re-suspends on any sibling still pending. Resolves the wait
    /// FIRST (the status CAS is the single-writer gate), then flips the run Suspended -> Pending and
    /// re-dispatches at once — the immediate path doesn't wait for sibling waits. Returns false (no-op)
    /// when this wait was already resolved — a deadline, a sibling/duplicate signal, or a Hangfire retry
    /// got there first — so every duplicate signal is idempotent.
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
    // its own payload. RESOLVE-FIRST (the wait's status CAS — not the run flip — is the single-writer
    // gate), then flip + dispatch: two branches resolving near-simultaneously each land their OWN payload
    // on their OWN wait first, so neither is lost to a run-flip race before resolving (the orphaned-wait
    // bug). This is the IMMEDIATE single-wait path — it re-dispatches as soon as THIS wait resolves, even
    // when a parallel sibling wait is still pending (the approval/action path re-walks at once); the
    // wait-for-all barrier is the wait-COMPLETION path's concern (ResumeOnWaitCompletionAsync). The bool
    // return is derived from whether THIS call resolved its wait — preserving every caller's contract.
    private async Task<bool> ResumeCoreAsync(Guid runId, string? resumePayloadJson, Guid waitId, CancellationToken cancellationToken)
    {
        // A bare timer wake supplies no payload, so we stamp a default marker; approval / callback /
        // action supply their decision body. (This is the ONLY difference from the wait-completion path.)
        var payload = resumePayloadJson ?? JsonSerializer.Serialize(new { resumed_at = DateTimeOffset.UtcNow.ToString("o") });

        var resolved = await ResolveWaitThenDispatchAsync(runId, waitId, payload, waitForAllPending: false, cancellationToken).ConfigureAwait(false);

        if (!resolved)
        {
            _logger.LogDebug("Resume: wait {WaitId} on run {RunId} already resolved — skipping (deadline / sibling / double signal)", waitId, runId);
            return false;
        }

        _logger.LogInformation("Resume: run {RunId} wait {WaitId} resolved (Suspended -> Pending -> dispatched)", runId, waitId);
        return true;
    }

    public async Task<bool> ResumeOnWaitCompletionAsync(Guid runId, Guid waitId, string resumePayloadJson, CancellationToken cancellationToken)
    {
        // A finished agent / child run MUST have its result consumed; the wait CAS below cannot drop it on a
        // sibling's run-flip race (the corruption where one completer's payload landed on every parallel
        // wait). Idempotent: an already-resolved wait (replay / double-notify) is a no-op. Unlike the
        // immediate path, this WAITS FOR ALL — it re-dispatches only once NO pending wait remains, so K
        // parallel completions advance the run exactly once, after the last.
        var resolved = await ResolveWaitThenDispatchAsync(runId, waitId, resumePayloadJson, waitForAllPending: true, cancellationToken).ConfigureAwait(false);

        if (!resolved)
        {
            _logger.LogDebug("Wait-completion resume: wait {WaitId} already resolved — skipping (replay / double-notify)", waitId);
            return false;
        }

        _logger.LogInformation("Wait-completion resume: run {RunId} wait {WaitId} resolved (dispatched once its last sibling wait resolved)", runId, waitId);
        return true;
    }

    // The single resolve-first concurrency core, shared by every resume signal (timer / approval /
    // callback / action via ResumeCoreAsync; agent / sub-workflow completion via ResumeOnWaitCompletionAsync).
    // Resolving the wait FIRST — not flipping the run first — is what makes K parallel waits safe: each
    // signal lands its OWN payload on its OWN wait, so none is orphaned by losing a run-flip CAS before it
    // resolved. Returns true when THIS call resolved the wait, false when it was already resolved (a deadline
    // / sibling / replay won) — the bool every caller's return contract is derived from, NOT the run flip.
    //
    // <paramref name="waitForAllPending"/> is the ONE divergence between the two entry points: the wait-
    // COMPLETION path (true) holds the run Suspended until the LAST sibling wait resolves (the wait-for-all
    // barrier — a durably-finished unit's result must never be missed by a partial re-walk); the IMMEDIATE
    // path (false) re-dispatches as soon as THIS wait resolves, so an approval/action click re-walks at once
    // and the still-suspended siblings re-park on the re-walk.
    private async Task<bool> ResolveWaitThenDispatchAsync(Guid runId, Guid waitId, string payload, bool waitForAllPending, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // 1. Status-guarded resolve of the ONE targeted wait. This CAS is the single-writer gate: 0 rows =
        //    the wait was already resolved (a deadline / sibling / double signal) → the caller no-ops. The
        //    walker injects payload_jsonb as the node's ResumePayload on re-run.
        var resolved = await _db.WorkflowRunWait
            .Where(w => w.Id == waitId && w.Status == WorkflowWaitStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkflowWaitStatuses.Resolved)
                .SetProperty(w => w.PayloadJson, payload)
                .SetProperty(w => w.ResolvedAt, (DateTimeOffset?)now), cancellationToken)
            .ConfigureAwait(false);

        if (resolved == 0) return false;

        // 2. Wait-for-all barrier (wait-completion path only): while any sibling wait is still pending the
        //    run stays Suspended, so no partial re-walk advances the run early or misses a wait resolved
        //    just after the walker passed its node (the stuck race). The immediate path skips this — it
        //    re-walks at once and the still-suspended siblings re-park.
        if (waitForAllPending)
        {
            var anyPending = await _db.WorkflowRunWait.AsNoTracking()
                .AnyAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending, cancellationToken)
                .ConfigureAwait(false);

            if (anyPending)
            {
                _logger.LogDebug("Resume: wait {WaitId} resolved; siblings still pending on run {RunId} — staying suspended", waitId, runId);
                return true;
            }
        }

        // Single-writer flip gate: only one resume flips Suspended -> Pending. A concurrent sibling that
        // already resolved its OWN wait (no orphan) but loses this CAS no-ops the dispatch — the winner's
        // single re-walk consumes every resolved wait (each keyed to its own node by the walker).
        var flipped = await _db.WorkflowRun
            .Where(r => r.Id == runId && r.Status == WorkflowRunStatus.Suspended)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Pending), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0)
        {
            _logger.LogDebug("Resume: run {RunId} no longer Suspended — a concurrent resume is driving the dispatch", runId);
            return true;
        }

        // Re-dispatch via the existing Pending -> Enqueued -> engine path, deferred until AFTER this
        // resume's transaction commits. RunAfterCommitAsync runs inline when there's no ambient transaction
        // (a timer / callback / deadline / agent-completion resume), but DEFERS when one is open (a human
        // respond folded into the RespondToMessageCommand transaction) — otherwise a worker could fetch the
        // job before the Suspended->Pending flip is visible and CAS-no-op it into a stuck wait. The walker
        // rehydrates and continues from the suspended node(s).
        await _postCommit.RunAfterCommitAsync(ct => _dispatcher.DispatchAsync(runId, ct), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Resume: run {RunId} re-dispatched after its wait resolved", runId);
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

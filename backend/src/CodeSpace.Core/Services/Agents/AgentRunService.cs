using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Mcp;
using CodeSpace.Core.Services.Agents.Sandbox;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Owns the durable lifecycle of an agent run: create it (Queued), flip it Running, append normalized
/// live-log events, heartbeat for liveness, and land a terminal result. The one place that reads/writes
/// <see cref="AgentRun"/> + <see cref="AgentRunEvent"/>, so a node, a worker, a reconciler, and the API
/// all drive runs through the same guarded transitions — no status flip bypasses
/// <see cref="AgentRunStateMachine"/>, and the run's xmin token stops two workers double-flipping it.
/// </summary>
public interface IAgentRunService
{
    /// <summary>Persist a new run in <see cref="AgentRunStatus.Queued"/> with <paramref name="task"/> as its envelope. workflowRunId/nodeId/iterationKey soft-link the owning workflow CELL — iterationKey is the spawning node's cell key (empty for a top-level node or a standalone run), so the N branches a map/loop fan-out spawns under one node stay distinguishable (D4 correlation spine).</summary>
    Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey = "", CancellationToken cancellationToken = default);

    /// <summary>
    /// Queued → Running; stamps StartedAt + an initial heartbeat, BUMPS the fencing epoch, and returns the new
    /// epoch — the claimer must complete under it (see the epoch overload of <see cref="CompleteAsync(Guid,AgentRunResult,long,CancellationToken)"/>),
    /// so a later reclaim (which bumps the epoch again) fences this claimer out. Throws
    /// <see cref="AgentRunTransitionException"/> when the run isn't Queued.
    /// </summary>
    Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>Refresh the liveness heartbeat a stuck-run reconciler reads. Idempotent; does not change status.</summary>
    Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Re-claim a still-<see cref="AgentRunStatus.Running"/> run for a fresh observer to re-attach to (its
    /// durable process is alive but its worker vanished): atomically BUMP the fencing epoch (so a revived
    /// original observer's epoch-fenced completion loses), INCREMENT the re-attach attempt counter (so the
    /// reconciler's ceiling can never lag the action — it's written in this same transaction), and stamp a FRESH
    /// lease + heartbeat (so the run drops out of the reconciler's stale-candidate set until the re-attaching
    /// worker renews it — no duplicate re-dispatch). Status-guarded CAS like <see cref="CompleteAsync(Guid,AgentRunResult,long,CancellationToken)"/>
    /// (a pure UPDATE, never a tracked save — so it can't strand a long run on optimistic concurrency). Returns
    /// whether it won the row (0 rows = no longer Running / another replica won → don't re-dispatch).
    /// </summary>
    Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// Record the run's durable runner handle (a <c>SandboxHandle</c> as JSON: pid + spool location +
    /// deadline) the instant it's launched, so a restarted backend can re-attach to or recover it from the
    /// spool rather than abandoning it. Idempotent set-based UPDATE (like <see cref="HeartbeatAsync"/>); does
    /// not change status.
    /// </summary>
    Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken);

    /// <summary>Append one normalized event to the run's append-only log. Sequence + timestamp are DB-assigned.</summary>
    Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken);

    /// <summary>Append a BATCH of events as ONE round-trip (AddRange + a single SaveChanges) — the hot-path write-cost fix for the agent tail loop, where one INSERT per line does not scale (and gets worse with faithful multi-block reasoning capture). The global BIGSERIAL <c>Sequence</c> is assigned by the DB in insertion order, so the batch preserves emission order. An empty batch is a no-op.</summary>
    Task AppendEventsAsync(Guid runId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken);

    /// <summary>Land a terminal result (the target state is <paramref name="result"/>'s Status); stores the result + CompletedAt. Throws when the transition is illegal or the result's status isn't terminal.</summary>
    Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken);

    /// <summary>
    /// Cancel a run that is still <see cref="AgentRunStatus.Queued"/> (staged but never launched) — the
    /// no-orphan path for a branch agent run whose parent workflow run reached a terminal state before its
    /// dispatch ran. A status-guarded CAS pinned to <c>Queued</c> (a pure UPDATE, like <see cref="HeartbeatAsync"/>),
    /// so it never races a worker that just claimed the run (that CAS already moved it to Running and loses 0
    /// rows here). Idempotent + safe from multiple replicas; <paramref name="reason"/> is stamped as the run's
    /// Error. Returns whether it won the row (false = already launched / already terminal → leave it alone).
    /// </summary>
    Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// Cancel a run that is actively <see cref="AgentRunStatus.Running"/> — the operator-cancel kill path for a
    /// branch agent whose sandbox process is already launched (the running-agent half of a run cancel, alongside
    /// <see cref="CancelQueuedAsync"/> for the not-yet-launched half). A Running-guarded CAS → <c>Cancelled</c>
    /// (a deliberate cancel, NOT Failed), epoch-fenced exactly like the reconciler's abandon: the kill is issued
    /// ONLY on a won CAS, so a run that legitimately completed in the same instant (a lost CAS) is never killed
    /// out from under its worker. On a won CAS it best-effort <c>TerminateAsync</c>s the durable process tree (so
    /// the orphaned agent stops holding its workspace + burning the injected model credential) — the kill failing
    /// never undoes the cancel. <paramref name="reason"/> is stamped as the run's Error. Returns whether it won
    /// the row (false = no longer Running — already terminal / not yet launched → leave it alone).
    /// </summary>
    Task<bool> CancelRunningAsync(Guid runId, string reason, CancellationToken cancellationToken);

    /// <summary>
    /// As <see cref="CompleteAsync(Guid,AgentRunResult,CancellationToken)"/>, but ALSO fenced on
    /// <paramref name="expectedEpoch"/> — the epoch the caller claimed with (<see cref="MarkRunningAsync"/>).
    /// The status-guarded CAS additionally requires the run still carries that epoch, so a worker whose run was
    /// reclaimed (its epoch bumped) and then revived matches 0 rows and throws, rather than double-completing.
    /// The executor uses this; an unfenced caller (a test, a direct admin path) uses the 3-arg overload.
    /// </summary>
    Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken);

    /// <summary>
    /// Read a run by id (untracked) — the SYSTEM/execution path (the executor loads any staged run to run
    /// it, regardless of team), so it is intentionally NOT team-scoped. Throws
    /// <see cref="KeyNotFoundException"/> when absent. An operator-facing read must use a team-scoped query.
    /// </summary>
    Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken);

    /// <summary>
    /// P3 (3.2c): the RESUMABLE session of the nearest ANCESTOR agent run at the same cell (<paramref name="nodeId"/>,
    /// <paramref name="iterationKey"/>) along this run's fork ancestry — walking <c>ParentRunId</c> up from
    /// <paramref name="parentRunId"/> (the run being forked from), NOT the flat root group, so a SIBLING rerun of the
    /// same original never resumes the other branch's conversation. Skips any ancestor that captured a session id but no
    /// transcript (not resumable), so a transcript-less nearer run never masks an older resumable one. Null when
    /// <paramref name="parentRunId"/> is null (a first run), no ancestor has a resumable session at the cell, or a
    /// prior's result is unreadable. Team-scoped on every hop. The CONTINUE producer stamps the result onto the new task.
    /// </summary>
    Task<ResumableSession?> FindResumableSessionAsync(Guid teamId, Guid? parentRunId, string nodeId, string iterationKey, CancellationToken cancellationToken);

    /// <summary>
    /// P3 (D1 retry-resume): the RESUMABLE session of the most-recent prior attempt of <paramref name="subtaskId"/> in
    /// <paramref name="supervisorRunId"/> (matched via the spawned agent's <see cref="AgentTask.SubtaskId"/>) — so a
    /// supervisor RETRY continues the failed attempt's conversation instead of restarting fresh. Null when there is no
    /// prior attempt of the subtask that captured a resumable session (both-or-neither) — the retry then cold-starts.
    /// </summary>
    Task<ResumableSession?> FindResumableSubtaskAttemptAsync(Guid teamId, Guid supervisorRunId, string subtaskId, CancellationToken cancellationToken);

    /// <summary>
    /// TEAM-SCOPED operator read of a run's live status (null when the run isn't <paramref name="teamId"/>'s,
    /// so a foreign id leaks nothing) — distinct from <see cref="GetAsync"/>, which is the un-scoped execution
    /// path. Backs the run-detail's live status header.
    /// </summary>
    Task<AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);

    /// <summary>
    /// Read the run's events with <c>Sequence &gt; afterSequence</c> in order — the operator-facing
    /// live-stream / replay cursor (pass 0 for the whole log). TEAM-SCOPED: returns an empty list when the
    /// run doesn't belong to <paramref name="teamId"/>, so a foreign run id leaks neither events nor its
    /// existence.
    /// </summary>
    Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken);
}

public sealed class AgentRunService : IAgentRunService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IAdmissionController _admissionController;
    private readonly ISandboxRunnerRegistry _runners;
    private readonly IArtifactOffloader _offloader;
    private readonly IToolCallLedgerService _ledger;
    private readonly Completion.ICompletionContractStore _contracts;
    private readonly ILogger<AgentRunService> _logger;

    public AgentRunService(CodeSpaceDbContext db, IAdmissionController admissionController, ISandboxRunnerRegistry runners, IArtifactOffloader offloader, IToolCallLedgerService ledger, Completion.ICompletionContractStore contracts, ILogger<AgentRunService> logger)
    {
        _db = db;
        _admissionController = admissionController;
        _runners = runners;
        _offloader = offloader;
        _ledger = ledger;
        _contracts = contracts;
        _logger = logger;
    }

    public async Task<AgentRun> CreateAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey = "", CancellationToken cancellationToken = default)
    {
        // Fail-closed admission gate (D4a): refuse the run BEFORE persisting if the team or the deployment is at
        // its in-flight cap. Throws AgentRunAdmissionException, which the engine wraps into a clean node failure
        // so an over-cap branch routes to its error edge / the map's continue-on-error rather than crashing.
        await _admissionController.EnsureAgentRunAdmittedAsync(teamId, cancellationToken).ConfigureAwait(false);

        var run = new AgentRun
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = workflowRunId,
            NodeId = nodeId,
            IterationKey = iterationKey,
            Harness = task.Harness,
            AgentDefinitionId = task.AgentDefinitionId,   // promoted from task_jsonb to a column so the runs index can filter by agent
            Status = AgentRunStatus.Queued,
            TaskJson = JsonSerializer.Serialize(task, AgentJson.Options),
        };

        _db.AgentRun.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent run created. RunId={RunId} Harness={Harness} TeamId={TeamId}", run.Id, run.Harness, teamId);

        await StakeWorkflowAgentObligationsAsync(task, teamId, workflowRunId, nodeId, iterationKey, cancellationToken).ConfigureAwait(false);

        return run;
    }

    /// <summary>
    /// P4-U1/U2 (workflow agent lanes onto the completion spine): a contract-bearing task dispatched OUTSIDE the
    /// supervisor (no WorkUnitRef — the supervisor stakes its own units at its own chokepoint) stakes ITS unit's
    /// obligations at authorization, exactly like a supervisor unit: acceptance Required plus delivery/output
    /// Required-or-authorized-NA per the task's own change expectation. The unit is the run's (node, iteration)
    /// coordinate, so a map's items stake DISTINCT rows instead of overwriting one root. Best-effort — a ledger
    /// fault never strands the dispatch; the composer reads an incomplete obligation set as Unknown.
    /// </summary>
    private async Task StakeWorkflowAgentObligationsAsync(AgentTask task, Guid teamId, Guid? workflowRunId, string? nodeId, string iterationKey, CancellationToken cancellationToken)
    {
        if (workflowRunId is not { } runId || task.Acceptance is null || task.WorkUnit is not null) return;

        try
        {
            var requirements = Supervisor.SupervisorUnitContract.BuildStakedRequirements(new[]
            {
                (AgentAcceptanceContract.UnitId(nodeId, iterationKey), AgentAcceptanceContract.Hash(task), AgentAcceptanceContract.ExpectsChanges(task)),
            });

            await _contracts.UpsertRequirementsAsync(runId, teamId, requirements, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Staking workflow-agent completion obligations failed for run {RunId}; dispatch proceeds", workflowRunId);
        }
    }

    public async Task<long> MarkRunningAsync(Guid runId, CancellationToken cancellationToken)
    {
        var run = await LoadAsync(runId, cancellationToken).ConfigureAwait(false);

        EnsureTransition(run, AgentRunStatus.Running);

        run.Status = AgentRunStatus.Running;
        run.StartedAt = DateTimeOffset.UtcNow;
        run.HeartbeatAt = DateTimeOffset.UtcNow;
        run.LeaseExpiresAt = AgentRunLiveness.NextLeaseExpiry();   // start the lease; the heartbeat renews it
        run.FenceEpoch += 1;   // bump on claim; the claimer completes under this epoch, so a later reclaim fences it out

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return run.FenceEpoch;
    }

    public async Task HeartbeatAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Tracking-free set-based UPDATE (like the reconciler's CAS). The executor pings this on a loop over
        // a long-lived scope, so a load-mutate-save would keep a tracked entity + a stale xmin between pings —
        // one lost optimistic-concurrency round would then silently kill every later heartbeat. A pure UPDATE
        // never participates in optimistic concurrency; a missing row is a harmless 0-row no-op.
        //
        // Renews the lease alongside the heartbeat: a live worker pushes lease_expires_at forward every ping,
        // so the reconciler's lease-expiry reclaim only fires once the worker stops pinging (it died/hung).
        var now = DateTimeOffset.UtcNow;
        await _db.AgentRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.HeartbeatAt, (DateTimeOffset?)now)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)(now + AgentRunLiveness.LeaseDuration)), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<bool> ReclaimForReattachAsync(Guid runId, CancellationToken cancellationToken)
    {
        // Tracking-free status-guarded CAS (like CompleteCoreAsync, NOT MarkRunningAsync's tracked load-save — a
        // tracked save would attach a stale-xmin entity to the reconciler's shared DbContext and could fail
        // optimistic concurrency against the executor's concurrent heartbeat). Atomic in-DB increment of the epoch
        // (the column-expression SetProperty form) fences a revived original observer; the fresh lease + heartbeat
        // take the run out of the stale sweep until the re-attaching worker keeps renewing it. 0 rows = the run is
        // no longer Running (another replica reclaimed it, or it already landed terminal) → caller skips dispatch.
        var now = DateTimeOffset.UtcNow;
        var reclaimed = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == AgentRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.FenceEpoch, r => r.FenceEpoch + 1)
                .SetProperty(r => r.ReattachAttempts, r => r.ReattachAttempts + 1)
                .SetProperty(r => r.HeartbeatAt, (DateTimeOffset?)now)
                .SetProperty(r => r.LeaseExpiresAt, (DateTimeOffset?)(now + AgentRunLiveness.LeaseDuration)), cancellationToken)
            .ConfigureAwait(false);

        return reclaimed == 1;
    }

    public async Task SetRunnerHandleAsync(Guid runId, string handleJson, CancellationToken cancellationToken)
    {
        // Tracking-free set-based UPDATE (like HeartbeatAsync): a pure UPDATE that never participates in
        // optimistic concurrency. Safe to bump the row independently of the executor's tracked instance because
        // CompleteAsync flips status via a status-guarded CAS (not the xmin token), so neither this write nor the
        // heartbeat's pings can block completion. A missing row is a harmless no-op.
        await _db.AgentRun
            .Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.RunnerHandleJson, handleJson), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<AgentRunEvent> AppendEventAsync(Guid runId, AgentEvent @event, CancellationToken cancellationToken)
    {
        var record = new AgentRunEvent
        {
            Id = Guid.NewGuid(),
            AgentRunId = runId,
            Kind = @event.Kind,
            Text = @event.Text,
            DataJson = @event.Data?.GetRawText(),
        };

        _db.AgentRunEvent.Add(record);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return record;
    }

    public async Task AppendEventsAsync(Guid runId, IReadOnlyList<AgentEvent> events, CancellationToken cancellationToken)
    {
        if (events.Count == 0) return;

        // ONE round-trip, ONE statement, with the per-run BIGSERIAL `sequence` assigned in STRICT emission order.
        // EF's batched AddRange does NOT preserve insert order for same-type rows — it sorts the modification
        // commands (by primary key, among other keys), and our PK is a random Guid, so the change tracker would
        // scramble the order in which Postgres assigns the serial. The live log + replay cursor both read by
        // `sequence`, so a scrambled serial = a scrambled stream. We pin the order in SQL instead: unnest the
        // three parallel arrays WITH ORDINALITY and INSERT … SELECT … ORDER BY ord, so Postgres produces (and
        // thus serial-stamps) the rows in array order. This holds because Postgres never parallelizes the writing
        // side of a single INSERT … SELECT — the ModifyTable node consumes the sorted stream row-by-row and calls
        // nextval() in that order; the ordering tests (single batch, cross-batch monotonicity, 300-row) guard it.
        // The parameter count is FIXED at five regardless of batch size (one array per column, fully bound — never
        // string-concatenated), so a 256-event flush is one bind, not hundreds of placeholders. `id` + `occurred_at`
        // use their column defaults (gen_random_uuid() / NOW()); append-only INSERT — the immutability trigger
        // (UPDATE/DELETE-only) is unaffected.
        var kinds = new string[events.Count];
        var texts = new string[events.Count];
        var data = new string?[events.Count];
        var dataArtifactIds = new Guid?[events.Count];

        for (var i = 0; i < events.Count; i++)
        {
            kinds[i] = events[i].Kind.ToString();   // matches the entity's HasConversion<string>() (enum member name)
            texts[i] = events[i].Text;
            data[i] = events[i].Data?.GetRawText();
        }

        await OffloadLargeDataPayloadsAsync(runId, data, dataArtifactIds, cancellationToken).ConfigureAwait(false);

        const string sql =
            "INSERT INTO agent_run_event (agent_run_id, kind, text, data_json, data_artifact_id) " +
            "SELECT {0}, e.kind, e.text, CAST(e.data AS jsonb), e.data_artifact_id " +
            "FROM unnest({1}::text[], {2}::text[], {3}::text[], {4}::uuid[]) WITH ORDINALITY AS e(kind, text, data, data_artifact_id, ord) " +
            "ORDER BY e.ord";

        await _db.Database.ExecuteSqlRawAsync(sql, new object[] { runId, kinds, texts, data, dataArtifactIds }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// D2 #1: offload any oversize structured payload (data_json) in the batch to the content-addressed artifact
    /// store via the shared <see cref="IArtifactOffloader"/>, nulling the inline value + recording the ref in the
    /// parallel <paramref name="dataArtifactIds"/> array. The common case — every payload small or absent — does
    /// ZERO work (no team lookup, no store call), so the batched-write hot path is unchanged; the team id is
    /// resolved once, lazily, only when something actually needs offloading.
    /// </summary>
    private async Task OffloadLargeDataPayloadsAsync(Guid runId, string?[] data, Guid?[] dataArtifactIds, CancellationToken cancellationToken)
    {
        Guid? teamId = null;

        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] is not { } payload || System.Text.Encoding.UTF8.GetByteCount(payload) <= ArtifactStoreConfig.InlineThresholdBytes)
                continue;

            teamId ??= await _db.AgentRun.AsNoTracking().Where(r => r.Id == runId).Select(r => (Guid?)r.TeamId)
                .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new KeyNotFoundException($"AgentRun {runId} not found — cannot offload its event payload.");

            var (_, artifactId) = await _offloader.OffloadIfLargeAsync(teamId.Value, payload, "application/json", cancellationToken).ConfigureAwait(false);

            if (artifactId is { })
            {
                data[i] = null;
                dataArtifactIds[i] = artifactId;
            }
        }
    }

    public Task CompleteAsync(Guid runId, AgentRunResult result, CancellationToken cancellationToken) =>
        CompleteCoreAsync(runId, result, expectedEpoch: null, cancellationToken);

    public Task CompleteAsync(Guid runId, AgentRunResult result, long expectedEpoch, CancellationToken cancellationToken) =>
        CompleteCoreAsync(runId, result, expectedEpoch, cancellationToken);

    private async Task CompleteCoreAsync(Guid runId, AgentRunResult result, long? expectedEpoch, CancellationToken cancellationToken)
    {
        if (!AgentRunStateMachine.IsTerminal(result.Status))
            throw new AgentRunTransitionException($"AgentRunResult.Status must be terminal — got {result.Status}.");

        // Read the current status FRESH + untracked, then flip via a status-guarded CAS — NOT a tracked save on
        // the xmin token. The worker heartbeats its own run on a separate DbContext (HeartbeatAsync), bumping the
        // row's xmin every interval; a tracked Complete would then fail its optimistic-concurrency check on any
        // run that outlived one heartbeat (~window/3) and never land terminal — stranding it Running until the
        // reconciler abandoned it. Guarding on STATUS instead means the worker's own liveness pings can't block
        // its completion, while a genuine concurrent transition (the reconciler abandoning this run) still wins
        // the CAS and this side loses cleanly. Mirrors the reconciler's idempotent CAS.
        //
        // When expectedEpoch is supplied (the executor's claim epoch), the CAS ALSO requires the run still
        // carries that epoch — so a worker whose run was reclaimed (the reclaim bumps fence_epoch) and then
        // revived matches 0 rows and loses, instead of double-completing. A null epoch (an unfenced caller)
        // keeps the status-only guard.
        var snapshot = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Status, r.TeamId })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

        var current = snapshot.Status;

        // Completion contract: a run can NEVER land Succeeded while a decision it raised is still unanswered —
        // re-grade Succeeded → NeedsReview(NeedsDecision) so the unanswered ask isn't buried under "success". Enforced at
        // THIS choke point (every normal completion) AND mirrored in the reconciler's spool recovery, so the invariant
        // holds on every terminal write path. Only a would-be Succeeded needs the lookup; every other terminal passes through.
        if (result.Status == AgentRunStatus.Succeeded)
        {
            // A1 (HARD gate): a raised-but-unanswered decision can never be buried under a green Succeeded.
            var pendingDecisionId = await _ledger.FindBlockingDecisionIdAsync(runId, cancellationToken).ConfigureAwait(false);
            result = AgentCompletionContract.ApplyPendingDecision(result, pendingDecisionId);

            // A2 (BEST-EFFORT net, opt-in): if no decision fired but the agent's FINAL message reads as an unresolved
            // question handed back to the human, re-grade to NeedsReview(NeedsReview). A1 takes precedence — this runs
            // only while still Succeeded, so a concrete decision outranks the heuristic. Flag-gated default-OFF.
            if (result.Status == AgentRunStatus.Succeeded && FinalOutputReview.Enabled)
                result = FinalOutputReview.ReGrade(result);
        }

        if (!AgentRunStateMachine.IsLegalTransition(current, result.Status))
            throw new AgentRunTransitionException($"Illegal AgentRun transition {current} → {result.Status} (run {runId}).");

        // D2/D3: large fields (the unified diff, the faithful raw transcript) are offloaded to the content-addressed
        // artifact store (team-scoped) and only the ref kept — so result_jsonb stays bounded instead of carrying an
        // unbounded blob. Small fields stay inline. Done BEFORE serialize so the persisted result carries the refs.
        result = await OffloadLargePatchAsync(result, snapshot.TeamId, cancellationToken).ConfigureAwait(false);
        result = await OffloadLargeRepositoryPatchesAsync(result, snapshot.TeamId, cancellationToken).ConfigureAwait(false);
        result = await OffloadLargeTranscriptAsync(result, snapshot.TeamId, cancellationToken).ConfigureAwait(false);
        result = await OffloadLargeSessionTranscriptAsync(result, snapshot.TeamId, cancellationToken).ConfigureAwait(false);

        var resultJson = JsonSerializer.Serialize(result, AgentJson.Options);

        var flipped = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == current && (expectedEpoch == null || r.FenceEpoch == expectedEpoch))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, result.Status)
                .SetProperty(r => r.ResultJson, resultJson)
                .SetProperty(r => r.SessionId, result.SessionId)
                .SetProperty(r => r.Error, result.Error)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0)
            throw new AgentRunTransitionException($"AgentRun {runId} was no longer {current}{(expectedEpoch is { } e ? $" at epoch {e}" : "")} at completion — a concurrent transition or reclaim won the race.");

        _logger.LogInformation("Agent run completed. RunId={RunId} Status={Status}", runId, result.Status);
    }

    /// <summary>
    /// D2: if the unified diff is larger than the artifact inline threshold, offload it to the content-addressed
    /// artifact store (team-scoped) and return the result with <c>Patch</c> cleared + <c>PatchArtifactId</c> set —
    /// so <c>result_jsonb</c> stays bounded. A small/empty diff is returned unchanged (stays inline). Idempotent:
    /// the store dedups by sha, so a re-completion (reattach) reuses the same artifact id.
    /// </summary>
    private async Task<AgentRunResult> OffloadLargePatchAsync(AgentRunResult result, Guid teamId, CancellationToken cancellationToken)
    {
        var (inline, artifactId) = await _offloader.OffloadIfLargeAsync(teamId, result.Patch, "text/x-diff", cancellationToken).ConfigureAwait(false);

        return artifactId is null ? result : result with { Patch = inline, PatchArtifactId = artifactId };
    }

    /// <summary>
    /// D2 (multi-repo): offload EACH writable repo's per-repo diff exactly like the top-level patch — a per-repo diff
    /// larger than the inline threshold moves to the team-scoped artifact store, clearing its inline <c>Patch</c> +
    /// setting <c>PatchArtifactId</c>, so <c>result_jsonb</c> stays bounded even for a many-repo change set. A
    /// single-repo run has no <see cref="AgentRunResult.RepositoryResults"/> → a no-op (byte-identical). Idempotent
    /// (the store dedups by sha), so a re-completion reuses the same artifact ids.
    /// </summary>
    private async Task<AgentRunResult> OffloadLargeRepositoryPatchesAsync(AgentRunResult result, Guid teamId, CancellationToken cancellationToken)
    {
        if (result.RepositoryResults.Count == 0) return result;

        var offloaded = new List<RepositoryRunResult>(result.RepositoryResults.Count);

        foreach (var repo in result.RepositoryResults)
        {
            var (inline, artifactId) = await _offloader.OffloadIfLargeAsync(teamId, repo.Patch, "text/x-diff", cancellationToken).ConfigureAwait(false);

            offloaded.Add(artifactId is null ? repo : repo with { Patch = inline, PatchArtifactId = artifactId });
        }

        return result with { RepositoryResults = offloaded };
    }

    /// <summary>
    /// D3: offload the faithful raw transcript (usually larger than the inline threshold for a real run) to the
    /// artifact store, keeping only <see cref="AgentRunResult.TranscriptArtifactId"/> — the durable "replay the
    /// exact session" record, fetched on demand rather than bloating result_jsonb. Small/empty transcripts stay
    /// inline. Idempotent (sha-dedup), so a re-completion reuses the same artifact id.
    /// </summary>
    private async Task<AgentRunResult> OffloadLargeTranscriptAsync(AgentRunResult result, Guid teamId, CancellationToken cancellationToken)
    {
        var (inline, artifactId) = await _offloader.OffloadIfLargeAsync(teamId, result.Transcript, "text/plain", cancellationToken).ConfigureAwait(false);

        return artifactId is null ? result : result with { Transcript = inline, TranscriptArtifactId = artifactId };
    }

    /// <summary>
    /// P3: offload the RESUMABLE session transcript (the harness-native session file captured for a later CONTINUE)
    /// exactly like the stream-json transcript — a larger one moves to the team-scoped artifact store, clearing inline
    /// <see cref="AgentRunResult.SessionTranscript"/> + setting <see cref="AgentRunResult.SessionTranscriptArtifactId"/>,
    /// so <c>result_jsonb</c> stays bounded. Empty (no resumable transcript / not captured) → a no-op. Idempotent (the
    /// store dedups by sha) so a re-completion reuses the same artifact id.
    /// </summary>
    private async Task<AgentRunResult> OffloadLargeSessionTranscriptAsync(AgentRunResult result, Guid teamId, CancellationToken cancellationToken)
    {
        var (inline, artifactId) = await _offloader.OffloadIfLargeAsync(teamId, result.SessionTranscript, "application/x-ndjson", cancellationToken).ConfigureAwait(false);

        return artifactId is null ? result : result with { SessionTranscript = inline, SessionTranscriptArtifactId = artifactId };
    }

    public async Task<bool> CancelQueuedAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        // Tracking-free status-guarded CAS pinned to Queued (mirrors AbandonAsync's Running-guarded flip): a
        // pure UPDATE that never participates in optimistic concurrency. Queued → Cancelled is legal
        // (AgentRunStateMachine). If a worker already claimed the run (Queued → Running), this matches 0 rows
        // and loses cleanly — we never trample a run that's mid-flight. 0 rows = already launched / terminal.
        var cancelled = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == AgentRunStatus.Queued)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AgentRunStatus.Cancelled)
                .SetProperty(r => r.Error, reason)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (cancelled == 0) return false;

        _logger.LogInformation("Agent run cancelled while still queued. RunId={RunId} Reason={Reason}", runId, reason);
        return true;
    }

    public async Task<bool> CancelRunningAsync(Guid runId, string reason, CancellationToken cancellationToken)
    {
        // Read the run's epoch + handle FRESH + untracked, then flip via a status-guarded, epoch-fenced CAS pinned
        // to Running (mirrors the reconciler's AbandonAsync, but → Cancelled, a deliberate cancel, not Failed).
        // Fencing on the epoch we just read means a worker whose run was reclaimed (the reclaim bumped the epoch)
        // and then revived can't be killed by a cancel that observed the old epoch — and, crucially, a run that
        // legitimately completed in the same instant loses the CAS, so we never kill a finished run. 0 rows = no
        // longer Running at this epoch → leave it alone.
        var snapshot = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Id == runId)
            .Select(r => new { r.Status, r.FenceEpoch, r.RunnerHandleJson })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (snapshot is null || snapshot.Status != AgentRunStatus.Running) return false;

        var cancelled = await _db.AgentRun
            .Where(r => r.Id == runId && r.Status == AgentRunStatus.Running && r.FenceEpoch == snapshot.FenceEpoch)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, AgentRunStatus.Cancelled)
                .SetProperty(r => r.Error, reason)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (cancelled == 0) return false;

        // Won the CAS → kill the sandbox process tree so the orphaned agent stops holding its workspace + burning
        // the injected model credential. Best-effort (mirrors AbandonAsync's TerminateQuietlyAsync): the cancel
        // stands even if the kill can't be issued. Only a durable runner with a parseable handle can be killed; a
        // non-durable / handle-less run is already Cancelled and has no detached process to reap.
        var durable = ResolveDurableRunner(snapshot.RunnerHandleJson, out var handle);
        if (durable is not null && handle is not null)
            await TerminateQuietlyAsync(durable, handle, runId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Agent run cancelled while running. RunId={RunId} Reason={Reason}", runId, reason);
        return true;
    }

    /// <summary>Resolve the durable runner for a persisted handle, or null when the handle is absent/unparseable or its runner isn't durable (then there is no detached process to terminate). Mirrors the reconciler's resolver.</summary>
    private ISandboxDurableRunner? ResolveDurableRunner(string? handleJson, out SandboxHandle? handle)
    {
        handle = null;

        if (string.IsNullOrWhiteSpace(handleJson)) return null;

        SandboxHandle? parsed;
        try { parsed = JsonSerializer.Deserialize<SandboxHandle>(handleJson, AgentJson.Options); }
        catch (JsonException) { return null; }

        if (parsed is null) return null;

        handle = parsed;
        return _runners.All.FirstOrDefault(r => r.Kind == parsed.Kind) as ISandboxDurableRunner;
    }

    /// <summary>Kill the cancelled run's process tree via its durable handle, swallowing any failure (the run still reached Cancelled; at worst the process lingers to its deadline) so a kill error never propagates out of the cancel.</summary>
    private async Task TerminateQuietlyAsync(ISandboxDurableRunner durable, SandboxHandle handle, Guid runId, CancellationToken cancellationToken)
    {
        try { await durable.TerminateAsync(handle, cancellationToken).ConfigureAwait(false); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to terminate the process for cancelled run {RunId}; it may keep running until its wall-clock deadline", runId);
        }
    }

    public async Task<AgentRun> GetAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.AgentRun.AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

    public async Task<ResumableSession?> FindResumableSessionAsync(Guid teamId, Guid? parentRunId, string nodeId, string iterationKey, CancellationToken cancellationToken)
    {
        if (parentRunId is null) return null;   // a first run has no ancestor to resume — cold-start, zero queries

        // Walk the ANCESTOR chain (parent → grandparent → … → root), team-scoped, nearest-first. NOT the flat root group:
        // a sibling rerun of the same original is off this chain, so it can never contaminate this run's resume. Shallow
        // (rerun depth); each hop is a PK lookup. The Contains guard is a defensive stop against a corrupt parent cycle.
        var chain = new List<Guid>();

        for (Guid? cursor = parentRunId; cursor is { } id && !chain.Contains(id); )
        {
            chain.Add(id);
            cursor = await _db.WorkflowRun.AsNoTracking().Where(r => r.Id == id && r.TeamId == teamId).Select(r => r.ParentRunId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        }

        var chainIds = chain.Select(id => (Guid?)id).ToList();

        // Every agent run at this cell anywhere in the chain that captured a session id (team-scoped; served by idx_agent_run_workflow_run).
        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(a => a.TeamId == teamId && chainIds.Contains(a.WorkflowRunId) && a.NodeId == nodeId && a.IterationKey == iterationKey && a.SessionId != null)
            .Select(a => new { a.Id, a.WorkflowRunId, a.SessionId, a.ResultJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The NEAREST ancestor's RESUMABLE session — chain order (nearest first), skipping a captured-but-transcript-less
        // prior so it never masks an older resumable one, and treating a corrupt prior result as not-resumable (the
        // contract is cold-start, never a hard failure). Both-or-neither: a session id is resumable ONLY with a transcript.
        foreach (var runId in chain)
        {
            var candidate = candidates.FirstOrDefault(c => c.WorkflowRunId == runId);

            if (candidate is not null && TryResumable(candidate.Id, candidate.SessionId, candidate.ResultJson) is { } resumable) return resumable;
        }

        return null;
    }

    public async Task<ResumableSession?> FindResumableSubtaskAttemptAsync(Guid teamId, Guid supervisorRunId, string subtaskId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(subtaskId)) return null;

        // The prior attempts (session-bearing agent runs) in THIS supervisor run, newest first. The run's agent count is
        // bounded (maxTotalSpawns), so this per-run scan + task_jsonb subtask match is cheap (served by
        // idx_agent_run_workflow_run). The subtask id lives in task_jsonb; matched in memory after load.
        var candidates = await _db.AgentRun.AsNoTracking()
            .Where(a => a.TeamId == teamId && a.WorkflowRunId == supervisorRunId && a.SessionId != null)
            .OrderByDescending(a => a.CreatedDate).ThenByDescending(a => a.Id)
            .Select(a => new { a.Id, a.SessionId, a.ResultJson, a.TaskJson })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // The most-recent RESUMABLE prior attempt of THIS subtask — skip a captured-but-transcript-less attempt so it
        // never masks an older resumable one (the retried agent continues the subtask's conversation, else cold-starts).
        foreach (var candidate in candidates)
        {
            if (SubtaskIdOf(candidate.TaskJson) != subtaskId) continue;

            if (TryResumable(candidate.Id, candidate.SessionId, candidate.ResultJson) is { } resumable) return resumable;
        }

        return null;
    }

    /// <summary>
    /// A prior agent run's (id, session id, result json) → its RESUMABLE session, or null when not resumable: no
    /// session id, no transcript (both-or-neither — a resume without one fails "No conversation found"), or a
    /// corrupt/undeserializable result (cold-start, NEVER a throw). Normalizes an offloaded <c>""</c> transcript to a
    /// null inline so the ref case carries a clean null. Shared by the fork-cell lineage lookup and the supervisor
    /// subtask-attempt lookup.
    /// </summary>
    private static ResumableSession? TryResumable(Guid agentRunId, string? sessionId, string? resultJson)
    {
        if (sessionId is not { Length: > 0 } sid || resultJson is null) return null;

        AgentRunResult? result;
        try { result = JsonSerializer.Deserialize<AgentRunResult>(resultJson, AgentJson.Options); }
        catch (JsonException) { return null; }

        var inline = result?.SessionTranscript is { Length: > 0 } t ? t : null;
        var artifactId = result?.SessionTranscriptArtifactId;

        return inline is not null || artifactId is not null ? new ResumableSession(agentRunId, sid, inline, artifactId) : null;
    }

    /// <summary>The <see cref="AgentTask.SubtaskId"/> a spawned agent's persisted task carries, or null (non-supervisor task / corrupt json).</summary>
    private static string? SubtaskIdOf(string taskJson)
    {
        try { return JsonSerializer.Deserialize<AgentTask>(taskJson, AgentJson.Options)?.SubtaskId; }
        catch (JsonException) { return null; }
    }

    public async Task<AgentRunSummary?> GetSummaryForTeamAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _db.AgentRun.AsNoTracking()
            .SingleOrDefaultAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        return run is null ? null : new AgentRunSummary
        {
            Id = run.Id,
            Status = run.Status,
            Harness = run.Harness,
            Goal = ReadGoal(run.TaskJson),
            Error = run.Error,
            StartedAt = run.StartedAt,
            HeartbeatAt = run.HeartbeatAt,
            CompletedAt = run.CompletedAt,
            CreatedDate = run.CreatedDate,
        };
    }

    /// <summary>The agent's GOAL (its instruction / prompt) off the durable task envelope — a NARROW projection of the one leaf, so the heavy task fields (workspace / permissions / repos) never materialize; null on an absent/malformed blob (best-effort, never throws).</summary>
    private static string? ReadGoal(string? taskJson)
    {
        if (string.IsNullOrEmpty(taskJson)) return null;

        try
        {
            var goal = JsonSerializer.Deserialize<GoalSlice>(taskJson, AgentJson.Options)?.Goal;
            return string.IsNullOrWhiteSpace(goal) ? null : goal;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record GoalSlice(string? Goal);

    public async Task<IReadOnlyList<AgentRunEvent>> GetEventsAsync(Guid runId, Guid teamId, long afterSequence, CancellationToken cancellationToken)
    {
        // Gate on ownership first so a foreign run id leaks neither events nor existence (return empty,
        // never throw "not found" only for owned runs — both cross-team and absent look identical).
        var owned = await _db.AgentRun.AsNoTracking()
            .AnyAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);

        if (!owned) return Array.Empty<AgentRunEvent>();

        return await _db.AgentRunEvent.AsNoTracking()
            .Where(e => e.AgentRunId == runId && e.Sequence > afterSequence)
            .OrderBy(e => e.Sequence)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentRun> LoadAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.AgentRun.SingleOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"AgentRun {runId} not found.");

    private static void EnsureTransition(AgentRun run, AgentRunStatus to)
    {
        if (!AgentRunStateMachine.IsLegalTransition(run.Status, to))
            throw new AgentRunTransitionException($"Illegal AgentRun transition {run.Status} → {to} (run {run.Id}).");
    }
}

/// <summary>An agent run was asked to make a transition its lifecycle doesn't allow (completing a Queued run, re-running a terminal one, a non-terminal completion status, …). A node/handler surfaces this as a clean failure.</summary>
public sealed class AgentRunTransitionException : Exception
{
    public AgentRunTransitionException(string message) : base(message) { }
}

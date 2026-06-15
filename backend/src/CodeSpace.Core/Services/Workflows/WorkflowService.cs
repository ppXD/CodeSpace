using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Nodes;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows;

/// <summary>
/// Service-layer logic for workflow CRUD + manual-run dispatch + run-history reads. The
/// pipeline shape per Rule 4: each public method is a flat sequence of named calls. Tenant
/// isolation is enforced by passing teamId through every method (controllers feed it from
/// the X-Team-Id header via ICurrentTeam).
/// </summary>
public sealed class WorkflowService : IWorkflowService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly DefinitionValidator _validator;
    private readonly INodeRegistry _nodeRegistry;
    private readonly Lifecycle.IRunRecordLogger _recordLogger;
    private readonly IRunStarter _runStarter;
    private readonly IWorkflowRunDispatcher _runDispatcher;
    private readonly Engine.IWorkflowResumeService _resumeService;
    private readonly IPostCommitActions _postCommit;
    private readonly IAgentRunService _agentRunService;
    private readonly ILogger<WorkflowService> _logger;

    /// <summary>Reason stamped on a branch agent run aborted by the kill-wave when an operator cancels its parent workflow run.</summary>
    private const string OperatorCancelledAgentReason = "Cancelled because its parent workflow run was cancelled by an operator.";

    public WorkflowService(CodeSpaceDbContext db, DefinitionValidator validator, INodeRegistry nodeRegistry, Lifecycle.IRunRecordLogger recordLogger, IRunStarter runStarter, IWorkflowRunDispatcher runDispatcher, Engine.IWorkflowResumeService resumeService, IPostCommitActions postCommit, IAgentRunService agentRunService, ILogger<WorkflowService> logger)
    {
        _db = db;
        _validator = validator;
        _nodeRegistry = nodeRegistry;
        _recordLogger = recordLogger;
        _runStarter = runStarter;
        _runDispatcher = runDispatcher;
        _resumeService = resumeService;
        _postCommit = postCommit;
        _agentRunService = agentRunService;
        _logger = logger;
    }

    public async Task<IReadOnlyList<WorkflowSummary>> ListAsync(Guid teamId, CancellationToken cancellationToken)
    {
        var workflows = await _db.Workflow
            .Where(w => w.TeamId == teamId && w.DeletedDate == null)
            .OrderByDescending(w => w.LastModifiedDate)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (workflows.Count == 0) return Array.Empty<WorkflowSummary>();

        var ids = workflows.Select(w => w.Id).ToList();

        var activationTypeKeysByWorkflowId = await _db.WorkflowActivation
            .Where(a => ids.Contains(a.WorkflowId) && a.DeletedDate == null)
            .GroupBy(a => a.WorkflowId)
            .Select(g => new { WorkflowId = g.Key, TypeKeys = g.Select(a => a.TypeKey).Distinct().ToList() })
            .ToDictionaryAsync(x => x.WorkflowId, x => (IReadOnlyList<string>)x.TypeKeys, cancellationToken).ConfigureAwait(false);

        return workflows.Select(w => new WorkflowSummary
        {
            Id = w.Id,
            TeamId = w.TeamId,
            Name = w.Name,
            Description = w.Description,
            Enabled = w.Enabled,
            LatestVersion = w.LatestVersion,
            CreatedDate = w.CreatedDate,
            LastModifiedDate = w.LastModifiedDate,
            ActivationTypeKeys = activationTypeKeysByWorkflowId.GetValueOrDefault(w.Id, Array.Empty<string>())
        }).ToList();
    }

    public async Task<WorkflowDetail?> GetAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken)
    {
        var workflow = await LoadWorkflowAsync(workflowId, teamId, cancellationToken).ConfigureAwait(false);
        if (workflow == null) return null;

        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(workflow.DefinitionJson, WorkflowJson.Options)
            ?? throw new InvalidOperationException($"Workflow {workflowId} has empty definition JSON.");

        var activations = await _db.WorkflowActivation
            .Where(a => a.WorkflowId == workflowId && a.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new WorkflowDetail
        {
            Id = workflow.Id,
            TeamId = workflow.TeamId,
            Name = workflow.Name,
            Description = workflow.Description,
            Enabled = workflow.Enabled,
            LatestVersion = workflow.LatestVersion,
            Definition = definition,
            Activations = activations.Select(MapActivation).ToList(),
            CreatedDate = workflow.CreatedDate,
            LastModifiedDate = workflow.LastModifiedDate
        };
    }

    public async Task<Guid> CreateAsync(Guid teamId, string name, string? description, WorkflowDefinition definition, IReadOnlyList<WorkflowActivationInput> activations, bool enabled, CancellationToken cancellationToken)
    {
        EnsureValidDefinition(definition);

        var workflowId = Guid.NewGuid();
        var definitionJson = JsonSerializer.Serialize(definition, WorkflowJson.Options);
        var now = DateTimeOffset.UtcNow;

        _db.Workflow.Add(new Workflow
        {
            Id = workflowId,
            TeamId = teamId,
            Name = name,
            Description = description,
            DefinitionJson = definitionJson,
            LatestVersion = 1,
            Enabled = enabled
        });

        // Every workflow_version row is a release: hash captures the canonical content,
        // CommittedAt seals the row (DB trigger rejects any later UPDATE/DELETE on a committed
        // row). DefinitionHash drives the replay-integrity check in WorkflowRun.
        _db.WorkflowVersion.Add(new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = 1,
            DefinitionJson = definitionJson,
            DefinitionHash = DefinitionHash.Compute(definition),
            CommittedAt = now,
            CreatedDate = now
        });

        foreach (var activation in activations) _db.WorkflowActivation.Add(BuildActivationRow(workflowId, activation));

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow created. WorkflowId={WorkflowId} TeamId={TeamId} Name={Name} Nodes={NodeCount} Edges={EdgeCount} Activations={ActivationCount} Enabled={Enabled}",
            workflowId, teamId, name, definition.Nodes.Count, definition.Edges.Count, activations.Count, enabled);

        return workflowId;
    }

    public async Task UpdateAsync(Guid workflowId, Guid teamId, string name, string? description, WorkflowDefinition definition, IReadOnlyList<WorkflowActivationInput> activations, CancellationToken cancellationToken)
    {
        EnsureValidDefinition(definition);

        var workflow = await LoadWorkflowAsync(workflowId, teamId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workflow {workflowId} not found in team {teamId}.");

        var newVersion = workflow.LatestVersion + 1;
        var definitionJson = JsonSerializer.Serialize(definition, WorkflowJson.Options);

        workflow.Name = name;
        workflow.Description = description;
        workflow.DefinitionJson = definitionJson;
        workflow.LatestVersion = newVersion;

        // Each update creates a new immutable release row. Existing rows are protected by the
        // workflow_version_enforce_immutability trigger; this INSERT is always for a NEW
        // version number (LatestVersion + 1) so the trigger doesn't fire.
        var nowUtc = DateTimeOffset.UtcNow;
        _db.WorkflowVersion.Add(new WorkflowVersion
        {
            WorkflowId = workflowId,
            Version = newVersion,
            DefinitionJson = definitionJson,
            DefinitionHash = DefinitionHash.Compute(definition),
            CommittedAt = nowUtc,
            CreatedDate = nowUtc
        });

        await ReplaceActivationsAsync(workflowId, activations, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow updated. WorkflowId={WorkflowId} TeamId={TeamId} NewVersion={Version} Nodes={NodeCount} Edges={EdgeCount} Activations={ActivationCount}",
            workflowId, teamId, newVersion, definition.Nodes.Count, definition.Edges.Count, activations.Count);
    }

    public async Task DeleteAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken)
    {
        var workflow = await LoadWorkflowAsync(workflowId, teamId, cancellationToken).ConfigureAwait(false);
        if (workflow == null) return;

        workflow.DeletedDate = DateTimeOffset.UtcNow;

        var activations = await _db.WorkflowActivation.Where(a => a.WorkflowId == workflowId && a.DeletedDate == null).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var a in activations) a.DeletedDate = DateTimeOffset.UtcNow;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow soft-deleted. WorkflowId={WorkflowId} TeamId={TeamId} ActivationsRemoved={ActivationCount}",
            workflowId, teamId, activations.Count);
    }

    public async Task SetEnabledAsync(Guid workflowId, Guid teamId, bool enabled, CancellationToken cancellationToken)
    {
        var workflow = await LoadWorkflowAsync(workflowId, teamId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workflow {workflowId} not found in team {teamId}.");

        var previous = workflow.Enabled;
        workflow.Enabled = enabled;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        if (previous != enabled)
        {
            _logger.LogInformation(
                "Workflow enable flag changed. WorkflowId={WorkflowId} TeamId={TeamId} {From} -> {To}",
                workflowId, teamId, previous, enabled);
        }
    }

    public async Task<Guid> RunManuallyAsync(Guid workflowId, Guid teamId, Guid actorUserId, JsonElement? payload, CancellationToken cancellationToken)
    {
        var workflow = await LoadWorkflowAsync(workflowId, teamId, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Workflow {workflowId} not found in team {teamId}.");

        var payloadJson = payload?.GetRawText() ?? "{}";

        // Unified run-start through IRunStarter. The manual / replay / webhook paths share
        // one (request + run + run.queued) staging path, varying only by the envelope
        // they hand in.
        var runId = await _runStarter.StartAsync(new RunSourceEnvelope
        {
            TeamId = teamId,
            WorkflowId = workflowId,
            WorkflowVersion = workflow.LatestVersion,
            SourceType = WorkflowRunSourceTypes.Manual,
            ActorType = WorkflowRunActorTypes.User,
            ActorId = actorUserId,
            NormalizedPayloadJson = payloadJson,
            CreatedBy = actorUserId,
        }, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Dispatch AFTER the command's transaction commits — RunAfterCommitAsync defers into the
        // post-commit drain while a transaction is open (the ICommand path here), so a Hangfire worker
        // can't fetch the job before the workflow_run row is visible (which would CAS-no-op and leave
        // the run stuck until the reconciler). The Pending→Enqueued CAS + the engine's Enqueued→Running
        // CAS still reject any duplicate; the reconciler backstops a dropped dispatch.
        await _postCommit.RunAfterCommitAsync(ct => _runDispatcher.DispatchAsync(runId, ct), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow manual run queued. WorkflowId={WorkflowId} TeamId={TeamId} RunId={RunId} Version={Version} PayloadSize={PayloadSize}",
            workflowId, teamId, runId, workflow.LatestVersion, payloadJson.Length);

        return runId;
    }

    public async Task<Guid> ReplayRunAsync(Guid originalRunId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        // Load original run + verify team ownership in one query — phantom or cross-team
        // runs return null and we conflate not-found with not-yours per the standard pattern.
        var original = await _db.WorkflowRun.AsNoTracking()
            .Include(r => r.RunRequest)
            .Where(r => r.Id == originalRunId && r.TeamId == teamId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"WorkflowRun {originalRunId} not found in team {teamId}.");

        // Replay re-runs through the authored (WorkflowId, Version) path. A snapshot run has neither
        // — replaying an inline-definition run is a later dynamic-workflows slice, not this substrate.
        if (original.WorkflowId is null)
            throw new NotSupportedException($"WorkflowRun {originalRunId} is a snapshot run (no parent workflow) and cannot be replayed yet.");

        // Clone the snapshot rows onto the new run id BEFORE we save the run. The engine
        // detects an existing snapshot to fork into the replay path; ordering matters less
        // since SaveChangesAsync is a single transaction, but writing snapshot+run in one
        // atomic batch keeps the engine from ever seeing a half-staged replay.
        var originalSnapshot = await _db.WorkflowRunVariable.AsNoTracking()
            .Where(v => v.RunId == originalRunId)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Replay envelope carries the lineage fields (CausationRequestId + ParentRunId +
        // ReleaseHashAtRun) so the unified starter writes them onto the request + run rows
        // for us. Replay-specific snapshot cloning still happens below because it lives
        // outside the (request + run) trio the starter owns.
        var replayRunId = await _runStarter.StartAsync(new RunSourceEnvelope
        {
            TeamId = teamId,
            WorkflowId = original.WorkflowId.Value,
            WorkflowVersion = original.WorkflowVersion!.Value,
            SourceType = WorkflowRunSourceTypes.Replay,
            ActorType = WorkflowRunActorTypes.User,
            ActorId = actorUserId,
            NormalizedPayloadJson = original.RunRequest?.NormalizedPayloadJson ?? "{}",
            CreatedBy = actorUserId,
            CausationRequestId = original.RunRequestId,
            ParentRunId = originalRunId,
            ReleaseHashAtRun = original.ReleaseHashAtRun,
        }, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var s in originalSnapshot)
        {
            _db.WorkflowRunVariable.Add(new WorkflowRunVariable
            {
                Id = Guid.NewGuid(),
                RunId = replayRunId,
                Scope = s.Scope,
                Name = s.Name,
                ValueType = s.ValueType,
                ValuePlain = s.ValuePlain,
                CapturedAt = now,
            });
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // Dispatch after commit (same as RunManuallyAsync). ReplayRunCommand is currently a non-
        // transactional IRequest, so RunAfterCommitAsync runs inline (the SaveChanges above already
        // auto-committed) — unchanged behavior today, and automatically post-commit-safe if replay is
        // ever folded into the transactional ICommand pipeline. Reconciler backstops a dropped dispatch.
        await _postCommit.RunAfterCommitAsync(ct => _runDispatcher.DispatchAsync(replayRunId, ct), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow run replay queued. OriginalRunId={Original} ReplayRunId={Replay} WorkflowId={Workflow} TeamId={Team} SnapshotCount={SnapshotCount}",
            originalRunId, replayRunId, original.WorkflowId, teamId, originalSnapshot.Count);

        return replayRunId;
    }

    public async Task<bool> ApproveRunAsync(Guid runId, Guid teamId, Guid actorUserId, bool approved, string? comment, CancellationToken cancellationToken)
    {
        // Tenancy — the run's workflow must belong to the caller's team (404 conflated with not-yours).
        var exists = await _db.WorkflowRun
            .AnyAsync(r => r.Id == runId && r.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        if (!exists) throw new KeyNotFoundException($"WorkflowRun {runId} not found in team {teamId}.");

        // Resolve the run's pending Approval wait — the OLDEST one, so two parallel approvals resolve
        // deterministically one click at a time and this run-level approve never collapses a sibling
        // approval / callback / timer wait. Approving a run parked only on a timer / callback, or one
        // that isn't suspended, finds no approval wait → no-op (the resume CAS would also reject it).
        var approvalWaitId = await _db.WorkflowRunWait
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.Approval)
            .OrderBy(w => w.CreatedAt)
            .Select(w => (Guid?)w.Id)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (approvalWaitId is null) return false;

        var payload = JsonSerializer.Serialize(new
        {
            approved,
            comment = comment ?? string.Empty,
            by = actorUserId.ToString(),
            resumed_at = DateTimeOffset.UtcNow.ToString("o"),
        });

        var resumed = await _resumeService.ResumeWaitAsync(runId, approvalWaitId.Value, payload, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Run approval. RunId={RunId} TeamId={TeamId} Approved={Approved} By={Actor} Resumed={Resumed}",
            runId, teamId, approved, actorUserId, resumed);

        return resumed;
    }

    public async Task<CancelRunOutcome?> CancelRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        // Tenancy + fail-closed: read the run's current status only if it's the caller's team. A foreign / phantom
        // run returns null (the controller maps that to 404) — never a silent success, never a leak of existence.
        var current = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId && r.TeamId == teamId)
            .Select(r => (WorkflowRunStatus?)r.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (current is null) return null;

        // Already terminal → idempotent no-op. Report the existing terminal status so the caller shows "already
        // finished" rather than a spurious cancel.
        if (WorkflowRunState.IsTerminal(current.Value))
            return new CancelRunOutcome { Cancelled = false, Status = current.Value, AgentRunsCancelled = 0 };

        // Status-guarded CAS from ANY non-terminal state → Cancelled (a pure UPDATE, not a tracked save on xmin, so
        // it never races the engine's own heartbeat-driven concurrency). 0 rows = the run reached a terminal state
        // between the read and the flip (the engine completed it, or a concurrent cancel won) → no-op, re-read.
        var flipped = await _db.WorkflowRun
            .Where(r => r.Id == runId && r.TeamId == teamId && r.Status == current.Value)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Cancelled)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);

        if (flipped == 0) return await ReReadTerminalOutcomeAsync(runId, teamId, cancellationToken).ConfigureAwait(false);

        var agentRunsCancelled = await TearDownCancelledRunAsync(runId, cancellationToken).ConfigureAwait(false);

        await _recordLogger.RunCancelledAsync(runId, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Workflow run cancelled by operator. RunId={RunId} TeamId={TeamId} From={From} AgentRunsCancelled={AgentRunsCancelled}", runId, teamId, current.Value, agentRunsCancelled);

        return new CancelRunOutcome { Cancelled = true, Status = WorkflowRunStatus.Cancelled, AgentRunsCancelled = agentRunsCancelled };
    }

    /// <summary>The flip lost the CAS (the run went terminal between read and write). Re-read its now-terminal status as a clean no-op outcome.</summary>
    private async Task<CancelRunOutcome> ReReadTerminalOutcomeAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var status = await _db.WorkflowRun.AsNoTracking()
            .Where(r => r.Id == runId && r.TeamId == teamId)
            .Select(r => r.Status)
            .SingleAsync(cancellationToken).ConfigureAwait(false);

        return new CancelRunOutcome { Cancelled = false, Status = status, AgentRunsCancelled = 0 };
    }

    /// <summary>
    /// Tear down a just-cancelled run, best-effort: KILL-WAVE its branch agent runs (Queued + Running), cancel its
    /// staged non-terminal sub-workflow children, and resolve its still-pending waits so none dangle. Best-effort end
    /// to end — one failed kill never aborts the cancel (the run is already Cancelled; the reconciler's parent-run-
    /// terminal guard re-cleans anything missed). Returns how many branch agent runs the kill-wave flipped.
    /// </summary>
    private async Task<int> TearDownCancelledRunAsync(Guid runId, CancellationToken cancellationToken)
    {
        try
        {
            var agentRunsCancelled = await KillWaveBranchAgentsAsync(runId, cancellationToken).ConfigureAwait(false);

            await CancelStagedSubworkflowChildrenAsync(runId, cancellationToken).ConfigureAwait(false);

            await CancelPendingWaitsAsync(runId, cancellationToken).ConfigureAwait(false);

            return agentRunsCancelled;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {RunId} was cancelled but tearing down its agents/children failed; the reconciler will catch any orphan", runId);
            return 0;
        }
    }

    /// <summary>
    /// The kill-wave: for every branch <c>AgentRun</c> the cancelled run spawned, abort it by its live status —
    /// Queued via the agent service's Queued-guarded CAS (a worker that just claimed it loses, untouched), Running
    /// via the epoch-fenced Running CAS + a durable process kill. Each is best-effort + idempotent; a worker landing
    /// a run terminal in the same instant simply loses the CAS, so no in-flight completion is trampled.
    /// </summary>
    private async Task<int> KillWaveBranchAgentsAsync(Guid runId, CancellationToken cancellationToken)
    {
        var branchAgents = await _db.AgentRun.AsNoTracking()
            .Where(r => r.WorkflowRunId == runId && (r.Status == AgentRunStatus.Queued || r.Status == AgentRunStatus.Running))
            .Select(r => new { r.Id, r.Status })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var cancelled = 0;

        foreach (var agent in branchAgents)
            if (await KillBranchAgentAsync(agent.Id, agent.Status, cancellationToken).ConfigureAwait(false)) cancelled++;

        return cancelled;
    }

    /// <summary>
    /// Kill one branch agent, closing the snapshot-vs-claim TOCTOU: a branch agent's executor is dispatched at
    /// suspend time, so one read Queued can be claimed Queued → Running between the snapshot and this call. We try
    /// the CAS for its snapshot status first; if a snapshot-Queued agent lost the Queued CAS (0 rows), re-read its
    /// LIVE status and fall through to <see cref="IAgentRunService.CancelRunningAsync"/> when it's now Running — so a
    /// concurrently-claimed agent is still killed rather than orphaned live under a Cancelled parent. The extra
    /// round-trip is paid ONLY on the lost-CAS path. Both CASes are idempotent + status/epoch-guarded, so at most
    /// one wins and a run another worker legitimately landed terminal is never trampled.
    /// </summary>
    private async Task<bool> KillBranchAgentAsync(Guid agentId, AgentRunStatus snapshotStatus, CancellationToken cancellationToken)
    {
        if (snapshotStatus == AgentRunStatus.Running)
            return await _agentRunService.CancelRunningAsync(agentId, OperatorCancelledAgentReason, cancellationToken).ConfigureAwait(false);

        if (await _agentRunService.CancelQueuedAsync(agentId, OperatorCancelledAgentReason, cancellationToken).ConfigureAwait(false))
            return true;

        // Lost the Queued CAS: a worker claimed it Queued → Running after the snapshot. Re-read its live status and
        // kill the now-Running agent (otherwise it runs on under a Cancelled parent — the exact orphan the kill-wave
        // exists to prevent). Any other status = already terminal → genuinely nothing to do.
        var liveStatus = await _db.AgentRun.AsNoTracking()
            .Where(r => r.Id == agentId)
            .Select(r => (AgentRunStatus?)r.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (liveStatus != AgentRunStatus.Running) return false;

        return await _agentRunService.CancelRunningAsync(agentId, OperatorCancelledAgentReason, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Cancel the run's staged non-terminal sub-workflow children (Pending/Enqueued → Cancelled CAS), mirroring the engine's source-side cleanup. Child runs that already started running / finished are left to their own lifecycle.</summary>
    private async Task CancelStagedSubworkflowChildrenAsync(Guid runId, CancellationToken cancellationToken)
    {
        var childRunIds = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Subworkflow && w.Status == WorkflowWaitStatuses.Pending)
            .Select(w => w.Token)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var ids = childRunIds.Select(t => Guid.TryParse(t, out var id) ? id : (Guid?)null).Where(id => id.HasValue).Select(id => id!.Value).ToList();

        if (ids.Count == 0) return;

        await _db.WorkflowRun
            .Where(r => ids.Contains(r.Id) && (r.Status == WorkflowRunStatus.Pending || r.Status == WorkflowRunStatus.Enqueued))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, WorkflowRunStatus.Cancelled)
                .SetProperty(r => r.CompletedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Resolve the cancelled run's still-pending waits so none dangle (the wait state is now moot — the run is
    /// terminal). Flips them <c>Resolved</c> with <c>ResolvedAt</c>, mirroring the engine's own terminal-cleanup
    /// (<c>CancelPendingWaitsAndChildrenAsync</c>) — the DB wait-status domain is Pending/Resolved (no Cancelled
    /// value), and a Resolved wait drops out of every reconciler sweep + the run-detail's resume affordance.
    /// </summary>
    private async Task CancelPendingWaitsAsync(Guid runId, CancellationToken cancellationToken)
    {
        await _db.WorkflowRunWait
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Status, WorkflowWaitStatuses.Resolved)
                .SetProperty(w => w.ResolvedAt, (DateTimeOffset?)DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<WorkflowRunSummary>> ListRunsAsync(Guid workflowId, Guid teamId, int limit, CancellationToken cancellationToken)
    {
        await EnsureWorkflowVisibleAsync(workflowId, teamId, cancellationToken).ConfigureAwait(false);

        var rows = await _db.WorkflowRun
            .Include(r => r.RunRequest)
            .Where(r => r.WorkflowId == workflowId)
            .OrderByDescending(r => r.CreatedDate)
            .Take(limit)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return rows.Select(r => new WorkflowRunSummary
        {
            Id = r.Id,
            WorkflowId = r.WorkflowId,
            WorkflowVersion = r.WorkflowVersion,
            SourceType = r.RunRequest?.SourceType ?? string.Empty,
            Status = r.Status,
            Error = r.Error,
            StartedAt = r.StartedAt,
            CompletedAt = r.CompletedAt,
            CreatedDate = r.CreatedDate
        }).ToList();
    }

    public async Task<WorkflowRunDetail?> GetRunAsync(Guid runId, Guid teamId, CancellationToken cancellationToken)
    {
        var run = await _db.WorkflowRun
            .Include(r => r.Workflow)
            .Include(r => r.RunRequest)
            .SingleOrDefaultAsync(r => r.Id == runId, cancellationToken)
            .ConfigureAwait(false);
        if (run == null) return null;

        // Tenancy via the denormalised run.TeamId — same value as run.Workflow.TeamId for an authored
        // run, and the ONLY team source for a snapshot run (which has no parent Workflow row).
        if (run.TeamId != teamId) return null;

        var nodes = await _db.WorkflowRunNode.Where(n => n.RunId == runId).OrderBy(n => n.StartedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var normalizedPayload = SafeParseJson(run.RunRequest?.NormalizedPayloadJson);
        var outputs = SafeParseJson(run.OutputsJson);

        // The outstanding wait (if any) — drives the run-detail's resume affordance. Latest
        // pending wins (there is at most one per node; a run parks on one at a time today).
        var pending = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        // Per-node child-run links: each flow.subworkflow node owns one Subworkflow wait whose
        // token is the child run id. The row survives resolution (status flips to Resolved), so
        // the link is available whether the step is still suspended or already finished.
        var childRunByNode = await LoadSubworkflowChildLinksAsync(runId, cancellationToken).ConfigureAwait(false);
        var agentRunByNode = await LoadAgentRunLinksAsync(runId, cancellationToken).ConfigureAwait(false);

        // nodeId → typeKey from the run's VERSION-PINNED definition (the exact JSON this run executed),
        // so each iterated row can be stamped with its owning container's kind. Disambiguates a map
        // branch key ("<mapId>#<i>") from a loop body key ("<loopId>#<i>"), which share a shape.
        var typeKeyByNodeId = await LoadNodeTypeKeysAsync(run, cancellationToken).ConfigureAwait(false);

        return new WorkflowRunDetail
        {
            Id = run.Id,
            WorkflowId = run.WorkflowId,
            WorkflowVersion = run.WorkflowVersion,
            SourceType = run.RunRequest?.SourceType ?? string.Empty,
            NormalizedPayload = normalizedPayload,
            Status = run.Status,
            Error = run.Error,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            Nodes = nodes.Select(n => MapRunNode(n, childRunByNode, agentRunByNode, typeKeyByNodeId)).ToList(),
            Outputs = outputs,
            PendingWait = pending == null ? null : new WorkflowRunWaitInfo
            {
                NodeId = pending.NodeId,
                Kind = pending.WaitKind,
                Token = pending.Token,
                WakeAt = pending.WakeAt,
                Payload = SafeParseJson(pending.PayloadJson),
            },
        };
    }

    public IReadOnlyList<NodeManifestDto> ListNodeManifests()
    {
        return _nodeRegistry.All.Select(n => new NodeManifestDto
        {
            TypeKey = n.TypeKey,
            DisplayName = n.Manifest.DisplayName,
            Category = n.Manifest.Category,
            Kind = n.Manifest.Kind,
            Description = n.Manifest.Description,
            IconKey = n.Manifest.IconKey,
            ConfigSchema = n.Manifest.ConfigSchema,
            InputSchema = n.Manifest.InputSchema,
            OutputSchema = n.Manifest.OutputSchema,
            IsManual = n.Manifest.IsManual,
            Presets = n.Manifest.Presets?.Select(p => new NodePresetDto { Id = p.Id, Label = p.Label, Description = p.Description, Config = p.Config, Inputs = p.Inputs }).ToList()
        }).ToList();
    }

    public IReadOnlyList<SystemVariableDto> ListSystemVariables()
    {
        return SystemScopeKeys.Descriptors
            .Select(d => new SystemVariableDto { Key = d.Key, Type = d.Type, Description = d.Description })
            .ToList();
    }

    private void EnsureValidDefinition(WorkflowDefinition definition)
    {
        var result = _validator.Validate(definition);
        if (result.IsValid) return;

        _logger.LogWarning(
            "Workflow definition rejected by validator. ErrorCount={ErrorCount} Errors={Errors}",
            result.Errors.Count, string.Join(" | ", result.Errors));

        throw new WorkflowValidationException(result.Errors);
    }

    private async Task<Workflow?> LoadWorkflowAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken)
    {
        return await _db.Workflow
            .SingleOrDefaultAsync(w => w.Id == workflowId && w.TeamId == teamId && w.DeletedDate == null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task EnsureWorkflowVisibleAsync(Guid workflowId, Guid teamId, CancellationToken cancellationToken)
    {
        var exists = await _db.Workflow.AnyAsync(w => w.Id == workflowId && w.TeamId == teamId && w.DeletedDate == null, cancellationToken).ConfigureAwait(false);
        if (!exists) throw new KeyNotFoundException($"Workflow {workflowId} not found in team {teamId}.");
    }

    private async Task ReplaceActivationsAsync(Guid workflowId, IReadOnlyList<WorkflowActivationInput> activations, CancellationToken cancellationToken)
    {
        var existing = await _db.WorkflowActivation.Where(a => a.WorkflowId == workflowId && a.DeletedDate == null).ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var a in existing) a.DeletedDate = DateTimeOffset.UtcNow;
        foreach (var input in activations) _db.WorkflowActivation.Add(BuildActivationRow(workflowId, input));
    }

    private static WorkflowActivation BuildActivationRow(Guid workflowId, WorkflowActivationInput input) => new()
    {
        Id = Guid.NewGuid(),
        WorkflowId = workflowId,
        TypeKey = input.TypeKey,
        ConfigJson = input.Config.GetRawText(),
        Enabled = input.Enabled
    };

    private static WorkflowActivationSummary MapActivation(WorkflowActivation a) => new()
    {
        Id = a.Id,
        TypeKey = a.TypeKey,
        Enabled = a.Enabled,
        Config = SafeParseJson(a.ConfigJson)
    };

    /// <summary>
    /// Maps each <c>flow.subworkflow</c> node to the child run it spawned. Keyed by
    /// <c>(NodeId, IterationKey)</c> so a sub-workflow inside a loop links each iteration to its own
    /// child. The Subworkflow wait row is unique per (run, node, iteration) and persists after it
    /// resolves, so the link holds for both suspended and completed steps.
    /// </summary>
    private async Task<IReadOnlyDictionary<(string NodeId, string IterationKey), string>> LoadSubworkflowChildLinksAsync(Guid runId, CancellationToken cancellationToken)
    {
        var waits = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Subworkflow)
            .Select(w => new { w.NodeId, w.IterationKey, w.Token })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return waits.ToDictionary(w => (w.NodeId, w.IterationKey), w => w.Token);
    }

    /// <summary>
    /// Maps each <c>agent.code</c> node to the agent run it spawned, keyed by <c>(NodeId, IterationKey)</c> —
    /// from the AgentRun wait row (its token IS the agent-run id), which persists post-resolution so the link
    /// holds whether the step is still suspended or finished. Lets the UI stream that run's live timeline.
    /// </summary>
    private async Task<IReadOnlyDictionary<(string NodeId, string IterationKey), string>> LoadAgentRunLinksAsync(Guid runId, CancellationToken cancellationToken)
    {
        var waits = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.AgentRun)
            .Select(w => new { w.NodeId, w.IterationKey, w.Token })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return waits.ToDictionary(w => (w.NodeId, w.IterationKey), w => w.Token);
    }

    private static WorkflowRunNodeSummary MapRunNode(
        WorkflowRunNode n,
        IReadOnlyDictionary<(string NodeId, string IterationKey), string> childRunByNode,
        IReadOnlyDictionary<(string NodeId, string IterationKey), string> agentRunByNode,
        IReadOnlyDictionary<string, string> typeKeyByNodeId) => new()
    {
        NodeId = n.NodeId,
        IterationKey = n.IterationKey,
        ContainerKind = ResolveContainerKind(n.IterationKey, typeKeyByNodeId),
        Status = n.Status,
        Inputs = SafeParseJson(n.InputsJson),
        Outputs = SafeParseJson(n.OutputsJson),
        Error = n.Error,
        StartedAt = n.StartedAt,
        CompletedAt = n.CompletedAt,
        ChildRunId = childRunByNode.GetValueOrDefault((n.NodeId, n.IterationKey)),
        AgentRunId = agentRunByNode.GetValueOrDefault((n.NodeId, n.IterationKey))
    };

    /// <summary>
    /// nodeId → typeKey for the run's VERSION-PINNED definition (the exact JSON it executed, not the
    /// workflow's current draft). Mirrors how the engine + <c>ActorIdentityRequirementGate</c> read the
    /// pinned <c>WorkflowVersion.DefinitionJson</c>. Empty when the version row is missing or unparsable
    /// (so every row falls back to a <c>null</c> container kind — no crash, just no badge).
    /// </summary>
    private async Task<IReadOnlyDictionary<string, string>> LoadNodeTypeKeysAsync(WorkflowRun run, CancellationToken cancellationToken)
    {
        // A snapshot run's definition is inline on the run row (no workflow_version to read); an
        // authored run reads its version-pinned JSON. Either way we map nodeId → typeKey from the
        // EXACT JSON this run executed.
        var definitionJson = run.DefinitionSnapshotJson ?? await _db.WorkflowVersion.AsNoTracking()
            .Where(v => v.WorkflowId == run.WorkflowId && v.Version == run.WorkflowVersion)
            .Select(v => v.DefinitionJson)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        var definition = definitionJson == null ? null : JsonSerializer.Deserialize<WorkflowDefinition>(definitionJson, WorkflowJson.Options);
        if (definition == null) return new Dictionary<string, string>();

        return definition.Nodes.ToDictionary(node => node.Id, node => node.TypeKey);
    }

    /// <summary>
    /// The <c>TypeKey</c> of the container that owns a row's INNERMOST iteration, or <c>null</c> for a
    /// top-level row. The engine builds each iteration-key segment as <c>"&lt;containerId&gt;#&lt;index&gt;"</c>
    /// (<c>/</c>-joined when nested), so the leaf container id is the text before the LAST <c>#</c> of the
    /// LAST <c>/</c>-segment — we look that id up in the pinned definition. An unknown id (forward-compat /
    /// stale version) yields <c>null</c>.
    /// </summary>
    private static string? ResolveContainerKind(string iterationKey, IReadOnlyDictionary<string, string> typeKeyByNodeId)
    {
        if (string.IsNullOrEmpty(iterationKey)) return null;

        var lastSegment = iterationKey[(iterationKey.LastIndexOf('/') + 1)..];
        var hash = lastSegment.LastIndexOf('#');
        if (hash <= 0) return null;

        var containerId = lastSegment[..hash];
        return typeKeyByNodeId.GetValueOrDefault(containerId);
    }

    /// <summary>Delegates to <see cref="WorkflowJsonSafeParse.SafeParse(string?)"/>.</summary>
    private static JsonElement SafeParseJson(string? raw) => WorkflowJsonSafeParse.SafeParse(raw);
}

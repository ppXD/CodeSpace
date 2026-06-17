using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly RunSources.IRunFromSnapshotStarter _snapshotStarter;
    private readonly IWorkflowRunDispatcher _runDispatcher;
    private readonly Engine.IWorkflowResumeService _resumeService;
    private readonly IPostCommitActions _postCommit;
    private readonly IAgentRunService _agentRunService;
    private readonly Rerun.IRerunCellSeeder _cellSeeder;
    private readonly ILogger<WorkflowService> _logger;

    /// <summary>Reason stamped on a branch agent run aborted by the kill-wave when an operator cancels its parent workflow run.</summary>
    private const string OperatorCancelledAgentReason = "Cancelled because its parent workflow run was cancelled by an operator.";

    public WorkflowService(CodeSpaceDbContext db, DefinitionValidator validator, INodeRegistry nodeRegistry, Lifecycle.IRunRecordLogger recordLogger, IRunStarter runStarter, RunSources.IRunFromSnapshotStarter snapshotStarter, IWorkflowRunDispatcher runDispatcher, Engine.IWorkflowResumeService resumeService, IPostCommitActions postCommit, IAgentRunService agentRunService, Rerun.IRerunCellSeeder cellSeeder, ILogger<WorkflowService> logger)
    {
        _db = db;
        _validator = validator;
        _nodeRegistry = nodeRegistry;
        _recordLogger = recordLogger;
        _runStarter = runStarter;
        _snapshotStarter = snapshotStarter;
        _runDispatcher = runDispatcher;
        _resumeService = resumeService;
        _postCommit = postCommit;
        _agentRunService = agentRunService;
        _cellSeeder = cellSeeder;
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
        var original = await LoadRunForForkAsync(originalRunId, teamId, cancellationToken).ConfigureAwait(false);
        var snapshot = await LoadVariableSnapshotAsync(originalRunId, cancellationToken).ConfigureAwait(false);

        var replayRunId = await StageForkedRunAsync(original, snapshot, WorkflowRunSourceTypes.Replay, teamId, actorUserId, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await DispatchAfterCommitAsync(replayRunId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow run replay queued. OriginalRunId={Original} ReplayRunId={Replay} Snapshot={IsSnapshot} TeamId={Team} SnapshotCount={SnapshotCount}",
            originalRunId, replayRunId, original.WorkflowId is null, teamId, snapshot.Count);

        return replayRunId;
    }

    public async Task<Guid> RerunFromNodeAsync(Guid originalRunId, string fromNodeId, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var original = await LoadRunForForkAsync(originalRunId, teamId, cancellationToken).ConfigureAwait(false);

        // Plan over the EXACT definition the original ran (snapshot inline JSON or the pinned authored version) —
        // never today's LatestVersion — so the re-run closure + the pre-seed set match what actually executed.
        var definition = await LoadOriginalDefinitionAsync(original, cancellationToken).ConfigureAwait(false);
        var plan = Rerun.RerunFromNodePlanner.Plan(definition, fromNodeId);

        // Fail-closed, ALL before any write. (a) no UNSUPPORTED node may be in the re-run closure — a
        // suspendable agent/subworkflow/supervisor node or a Map/Loop/Try container (re-running those isn't
        // supported yet); a side-effecting node IS allowed (the engine approval-gates it at runtime, D7-3);
        // (b) every KEPT (reused) node must have settled cleanly in the original so there is an output to carry.
        EnsureNoUnsupportedNodeInClosure(definition, plan);
        var keptToSeed = await ResolveReusableKeptCellsAsync(originalRunId, definition, plan, cancellationToken).ConfigureAwait(false);

        var snapshot = await LoadVariableSnapshotAsync(originalRunId, cancellationToken).ConfigureAwait(false);

        var rerunRunId = await StageForkedRunAsync(original, snapshot, WorkflowRunSourceTypes.Rerun, teamId, actorUserId, cancellationToken).ConfigureAwait(false);

        // Pre-seed the kept (reused) upstream cells onto the fork; the engine's RehydrateFromLedger then settles
        // them and the frontier resumes at fromNode. The sentinel (empty original snapshot) forces the replay
        // scope path. All these writes land in the command's ambient transaction; dispatch fires post-commit.
        await _cellSeeder.SeedKeptCellsAsync(originalRunId, rerunRunId, keptToSeed, writeEmptySnapshotSentinel: snapshot.Count == 0, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await DispatchAfterCommitAsync(rerunRunId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow run rerun-from-node queued. OriginalRunId={Original} RerunRunId={Rerun} FromNode={FromNode} ReRun={ReRunCount} Reused={ReusedCount} Snapshot={IsSnapshot} TeamId={Team}",
            originalRunId, rerunRunId, fromNodeId, plan.ReRunNodeIds.Count, keptToSeed.Count, original.WorkflowId is null, teamId);

        return rerunRunId;
    }

    public async Task<Guid> RerunMapBranchAsync(Guid originalRunId, string mapNodeId, int branchIndex, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        var original = await LoadRunForForkAsync(originalRunId, teamId, cancellationToken).ConfigureAwait(false);
        var definition = await LoadOriginalDefinitionAsync(original, cancellationToken).ConfigureAwait(false);

        // Fail-closed, ALL before any write. (1) the target must be a TOP-LEVEL flow.map; (2) its body must be
        // re-runnable (pure compute/read only — no side-effecting / suspendable / nested-container body node, v1);
        // (3) the original map must have SUCCEEDED (a Success map ⇒ every branch settled cleanly — this subsumes
        // "suspended sibling" and "terminate-mode-failed map", both non-Success → refuse); (4) the branch index
        // must be within the original fan-out. The plan re-runs FROM the map (so the map re-enters + its
        // downstream synthesizer re-runs); the gate exempts ONLY this map from the container refusal.
        EnsureTargetIsTopLevelMap(definition, mapNodeId);
        EnsureMapItemsReplayDeterministic(definition, mapNodeId);
        EnsureBranchBodyIsRerunnable(definition, mapNodeId);

        var plan = Rerun.RerunFromNodePlanner.Plan(definition, mapNodeId);
        EnsureNoUnsupportedNodeInClosure(definition, plan, exemptMapId: mapNodeId);

        await EnsureOriginalMapSucceededAsync(originalRunId, mapNodeId, cancellationToken).ConfigureAwait(false);
        var branchCount = await ResolveOriginalBranchCountAsync(originalRunId, mapNodeId, cancellationToken).ConfigureAwait(false);
        EnsureBranchIndexInRange(branchIndex, branchCount, mapNodeId);

        // The map is the re-run root, so it + its downstream are RE-RUN; everything upstream is KEPT (incl. the
        // node binding the map's items — guaranteeing the fork re-resolves the SAME element array, so the seeded
        // sibling indices align). Seed the kept upstream (D7-2) + the N-1 NON-target sibling branch cells.
        var keptToSeed = await ResolveReusableKeptCellsAsync(originalRunId, definition, plan, cancellationToken).ConfigureAwait(false);
        var snapshot = await LoadVariableSnapshotAsync(originalRunId, cancellationToken).ConfigureAwait(false);

        var rerunRunId = await StageForkedRunAsync(original, snapshot, WorkflowRunSourceTypes.Rerun, teamId, actorUserId, cancellationToken).ConfigureAwait(false);

        await _cellSeeder.SeedKeptCellsAsync(originalRunId, rerunRunId, keptToSeed, writeEmptySnapshotSentinel: snapshot.Count == 0, cancellationToken).ConfigureAwait(false);
        await _cellSeeder.SeedSiblingBranchCellsAsync(originalRunId, rerunRunId, mapNodeId, branchIndex, branchCount, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await DispatchAfterCommitAsync(rerunRunId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow map-branch rerun queued. OriginalRunId={Original} RerunRunId={Rerun} Map={Map} Branch={Branch}/{BranchCount} Reused={ReusedCount} Snapshot={IsSnapshot} TeamId={Team}",
            originalRunId, rerunRunId, mapNodeId, branchIndex, branchCount, keptToSeed.Count, original.WorkflowId is null, teamId);

        return rerunRunId;
    }

    /// <summary>Gate: <paramref name="mapNodeId"/> must resolve to a TOP-LEVEL (ParentId==null) node whose manifest Kind is Map. Else 400.</summary>
    private void EnsureTargetIsTopLevelMap(WorkflowDefinition definition, string mapNodeId)
    {
        var node = definition.Nodes.FirstOrDefault(n => n.Id == mapNodeId)
            ?? throw new RerunTargetNotFoundException($"Map node '{mapNodeId}' not found in the run's definition.");

        if (node.ParentId != null)
            throw new RerunTargetNotFoundException($"Node '{mapNodeId}' is inside a container; only a top-level map's branch can be re-run.");

        if (_nodeRegistry.Resolve(node.TypeKey).Manifest.Kind != NodeKind.Map)
            throw new RerunTargetNotFoundException($"Node '{mapNodeId}' is not a map; map-branch rerun requires a flow.map target.");
    }

    // The map's items binding re-resolves at execution time from scope. On a fork, BuildScopeForReplay FREEZES
    // trigger.* + plain wf/team + upstream node outputs (all KEPT+seeded), but RE-RESOLVES project.* and
    // secret-typed wf/team LIVE (project is never snapshotted; secrets honour rotation). If the map's items bind
    // to a live-re-resolved scope, an operator editing that variable between runs would change the branch space
    // — and the seeded siblings (indexed by the ORIGINAL fan-out) would misalign with the fork's elements →
    // silent wrong-element attribution. So sibling reuse is sound ONLY when items resolve from a frozen/kept
    // source (trigger / upstream node output / a literal array). Refuse the live-re-resolved scopes, fail-closed.
    private static readonly Regex LiveReResolvedItemsScope = new(@"\{\{\s*(project|wf|team|sys)\.", RegexOptions.Compiled);

    /// <summary>Gate: refuse when the map's <c>items</c> bind to a scope that re-resolves live on replay (project / wf /
    /// team / sys) — the branch space could differ on rerun, making sibling reuse unsound. A literal array or a
    /// trigger / upstream-node binding is frozen-or-kept and deterministic, so it is allowed.</summary>
    private static void EnsureMapItemsReplayDeterministic(WorkflowDefinition definition, string mapNodeId)
    {
        var mapNode = definition.Nodes.First(n => n.Id == mapNodeId);   // existence already enforced by EnsureTargetIsTopLevelMap

        if (mapNode.Inputs.ValueKind != JsonValueKind.Object
            || !mapNode.Inputs.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.String)
            return;   // a literal array / absent items is frozen in the definition → deterministic on replay

        if (LiveReResolvedItemsScope.IsMatch(items.GetString() ?? string.Empty))
            throw new RerunUpstreamNotReusableException(
                $"Map '{mapNodeId}' binds its items to a variable scope (project / wf / team / sys) that is re-resolved live on replay; the branch space could differ on rerun, so reusing the sibling branches would be unsound. Replay the whole run instead.");
    }

    /// <summary>Gate (v1): a re-run map branch body may contain only pure compute/read nodes — refuse a side-effecting,
    /// suspendable, or nested-container body node (the D7-3-gate-inside-a-branch + agent-re-stage compositions are
    /// deferred to a follow-up). Scans the static body subgraph (ParentId == mapNodeId), fail-closed.</summary>
    private void EnsureBranchBodyIsRerunnable(WorkflowDefinition definition, string mapNodeId)
    {
        var blocked = definition.Nodes
            .Where(n => n.ParentId == mapNodeId)
            .Where(n =>
            {
                var m = _nodeRegistry.Resolve(n.TypeKey).Manifest;
                return m.IsSideEffecting || m.CanSuspend || m.Kind is NodeKind.Map or NodeKind.Loop or NodeKind.Try;
            })
            .Select(n => n.Id)
            .OrderBy(id => id)
            .ToList();

        if (blocked.Count > 0) throw new RerunBlockedByUnsupportedNodeException(blocked);
    }

    /// <summary>Gate: the original map must have COMPLETED Successfully. A Success map guarantees every branch settled
    /// cleanly (the engine re-suspends the map while any branch is parked, and terminate-mode failures fail the map),
    /// so this single check subsumes "a sibling is still suspended/running" and "terminate-mode map failed" — both
    /// leave the map non-Success → no clean aggregate to reuse → 422.</summary>
    private async Task EnsureOriginalMapSucceededAsync(Guid originalRunId, string mapNodeId, CancellationToken cancellationToken)
    {
        var status = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == originalRunId && n.NodeId == mapNodeId && n.IterationKey == WorkflowIterationKeys.TopLevel)
            .Select(n => (NodeStatus?)n.Status)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (status != NodeStatus.Success)
            throw new RerunUpstreamNotReusableException(
                $"Cannot re-run a branch of map '{mapNodeId}': it did not complete successfully in run {originalRunId} (status {status?.ToString() ?? "never ran"}). Replay the whole run instead.");
    }

    /// <summary>The number of branches the original map fanned out over = the count of DISTINCT direct branch keys
    /// (<c>"&lt;mapId&gt;#&lt;i&gt;"</c>, integer i, no nested '/'). Branches are contiguous 0..N-1, so the distinct count is N.</summary>
    private async Task<int> ResolveOriginalBranchCountAsync(Guid originalRunId, string mapNodeId, CancellationToken cancellationToken)
    {
        var prefix = $"{mapNodeId}#";
        var keys = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == originalRunId && n.IterationKey.StartsWith(prefix))
            .Select(n => n.IterationKey)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        // Keep only DIRECT branch keys "<mapId>#<int>" (a nested descendant has "<mapId>#<i>/<inner>#<j>").
        var directIndices = new HashSet<int>();
        foreach (var key in keys)
        {
            var rest = key[prefix.Length..];
            if (!rest.Contains('/') && int.TryParse(rest, out var i) && i >= 0) directIndices.Add(i);
        }

        return directIndices.Count;
    }

    private static void EnsureBranchIndexInRange(int branchIndex, int branchCount, string mapNodeId)
    {
        if (branchIndex < 0 || branchIndex >= branchCount)
            throw new RerunTargetNotFoundException(
                $"Branch index {branchIndex} is out of range for map '{mapNodeId}' (the original fanned out over {branchCount} branch(es), valid indices 0..{branchCount - 1}).");
    }

    // ── Fork helpers — shared by replay + rerun (both fork a NEW run from the original; never mutate it) ──

    /// <summary>Load the original run (with its request) verifying team ownership; phantom / cross-team conflate to KeyNotFound (404).</summary>
    private async Task<WorkflowRun> LoadRunForForkAsync(Guid originalRunId, Guid teamId, CancellationToken cancellationToken) =>
        await _db.WorkflowRun.AsNoTracking()
            .Include(r => r.RunRequest)
            .Where(r => r.Id == originalRunId && r.TeamId == teamId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"WorkflowRun {originalRunId} not found in team {teamId}.");

    private async Task<List<WorkflowRunVariable>> LoadVariableSnapshotAsync(Guid runId, CancellationToken cancellationToken) =>
        await _db.WorkflowRunVariable.AsNoTracking().Where(v => v.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Stage a forked run from the original + clone its variable snapshot — STAGE-ONLY (caller saves + dispatches).
    /// The ONLY thing that differs by source is the definition: an authored run re-pins (WorkflowId, Version); a
    /// snapshot run carries its frozen definition inline (no re-validate/re-freeze — the engine's tamper-check
    /// passes because the hash travels with it). The cloned snapshot makes the engine take the REPLAY scope path
    /// (frozen plains + re-resolved secrets). Lineage (ParentRunId + causation) is stamped for both.
    /// </summary>
    private async Task<Guid> StageForkedRunAsync(WorkflowRun original, List<WorkflowRunVariable> snapshot, string sourceType, Guid teamId, Guid actorUserId, CancellationToken cancellationToken)
    {
        Guid forkRunId;
        if (original.WorkflowId is null)
            forkRunId = await _snapshotStarter.StageReplayFromSnapshotAsync(
                original.DefinitionSnapshotJson ?? throw new InvalidOperationException($"Snapshot run {original.Id} has no inline definition to fork."),
                original.DefinitionSnapshotHash ?? string.Empty,
                teamId, actorUserId, original.RunRequest?.NormalizedPayloadJson ?? "{}", sourceType,
                parentRunId: original.Id, causationRequestId: original.RunRequestId, cancellationToken).ConfigureAwait(false);
        else
            forkRunId = await _runStarter.StartAsync(new RunSourceEnvelope
            {
                TeamId = teamId,
                WorkflowId = original.WorkflowId.Value,
                WorkflowVersion = original.WorkflowVersion!.Value,
                SourceType = sourceType,
                ActorType = WorkflowRunActorTypes.User,
                ActorId = actorUserId,
                NormalizedPayloadJson = original.RunRequest?.NormalizedPayloadJson ?? "{}",
                CreatedBy = actorUserId,
                CausationRequestId = original.RunRequestId,
                ParentRunId = original.Id,
                ReleaseHashAtRun = original.ReleaseHashAtRun,
            }, cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var s in snapshot)
            _db.WorkflowRunVariable.Add(new WorkflowRunVariable
            {
                Id = Guid.NewGuid(),
                RunId = forkRunId,
                Scope = s.Scope,
                Name = s.Name,
                ValueType = s.ValueType,
                ValuePlain = s.ValuePlain,
                CapturedAt = now,
            });

        return forkRunId;
    }

    /// <summary>
    /// Dispatch after the (transactional) commit, mirroring RunManuallyAsync — the command pipeline's
    /// TransactionalBehavior owns the commit, so RunAfterCommitAsync fires the dispatch only once the run's rows
    /// (and a rerun's pre-seeded cells) are durable; a worker can never pick up the run before then. The
    /// reconciler backstops a dropped dispatch.
    /// </summary>
    private async Task DispatchAfterCommitAsync(Guid runId, CancellationToken cancellationToken) =>
        await _postCommit.RunAfterCommitAsync(ct => _runDispatcher.DispatchAsync(runId, ct), cancellationToken).ConfigureAwait(false);

    /// <summary>Deserialize the EXACT definition the original ran — a snapshot run's inline frozen JSON, or an authored run's pinned version JSON. Mirrors the engine's definition-source fork.</summary>
    private async Task<WorkflowDefinition> LoadOriginalDefinitionAsync(WorkflowRun original, CancellationToken cancellationToken)
    {
        var json = original.WorkflowId is null
            ? original.DefinitionSnapshotJson ?? throw new InvalidOperationException($"Snapshot run {original.Id} has no inline definition.")
            : (await _db.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == original.WorkflowId && v.Version == original.WorkflowVersion, cancellationToken).ConfigureAwait(false)).DefinitionJson;

        return System.Text.Json.JsonSerializer.Deserialize<WorkflowDefinition>(json, WorkflowJson.Options)
            ?? throw new InvalidOperationException($"Run {original.Id} has an empty definition.");
    }

    /// <summary>
    /// FAIL-CLOSED rerun-capability gate: a re-run node whose re-execution isn't supported yet is refused —
    /// a SUSPENDABLE node (<c>CanSuspend</c> — agent.code / subworkflow / supervisor / chat.post_message with
    /// waitForResponse, which would re-stage an agent run / child / decision / interactive wait) or a CONTAINER
    /// (a Map/Loop/Try, which would re-run its whole body). A purely SIDE-EFFECTING node (IsSideEffecting but not
    /// CanSuspend) is NOT refused here: the engine approval-gates it at runtime (D7-3); a node that is BOTH
    /// side-effecting and suspendable is refused via the CanSuspend arm (fail-closed wins). A node UPSTREAM
    /// (kept + reused, never re-executed) is always fine; only the re-run closure is scanned.
    /// </summary>
    private void EnsureNoUnsupportedNodeInClosure(WorkflowDefinition definition, RerunPlan plan, string? exemptMapId = null)
    {
        var typeByNode = definition.Nodes.ToDictionary(n => n.Id, n => n.TypeKey);

        var blocked = plan.ReRunNodeIds
            .Where(id => typeByNode.TryGetValue(id, out var typeKey) && IsRerunUnsupported(_nodeRegistry.Resolve(typeKey).Manifest, id, exemptMapId))
            .OrderBy(id => id)
            .ToList();

        if (blocked.Count > 0) throw new RerunBlockedByUnsupportedNodeException(blocked);
    }

    /// <summary><paramref name="exemptMapId"/> (D7-4) exempts EXACTLY that one map id from the container arm — it is the
    /// rerun-by-branch target, which re-enters + replays its siblings. Every OTHER map, every Loop/Try, and every
    /// suspendable node stays refused (fail-closed). The from-node path passes null → no exemption (byte-identical).</summary>
    private static bool IsRerunUnsupported(NodeManifest manifest, string nodeId, string? exemptMapId) =>
        manifest.CanSuspend
        || (manifest.Kind == NodeKind.Map && nodeId != exemptMapId)
        || manifest.Kind is NodeKind.Loop or NodeKind.Try;

    /// <summary>
    /// The kept (reused) top-level cells to pre-seed = the plan's KEPT set intersected with the original's
    /// actually-settled cells. A kept node that settled cleanly (Success / Skipped / Failure-with-error-edge) is
    /// reused; a kept node that did NOT settle cleanly (Running / Failure-without-error-edge) is a hard refuse —
    /// there is no reusable outcome. A kept node with NO cell never ran in the original (a dead branch); it is
    /// neither seeded nor refused — the engine re-skips it via edge-liveness from the reused routing hints.
    /// </summary>
    private async Task<IReadOnlySet<string>> ResolveReusableKeptCellsAsync(Guid originalRunId, WorkflowDefinition definition, RerunPlan plan, CancellationToken cancellationToken)
    {
        if (plan.KeptNodeIds.Count == 0) return plan.KeptNodeIds;

        var cells = await _db.WorkflowRunNode.AsNoTracking()
            .Where(n => n.RunId == originalRunId && n.IterationKey == WorkflowIterationKeys.TopLevel && plan.KeptNodeIds.Contains(n.NodeId))
            .ToDictionaryAsync(n => n.NodeId, cancellationToken).ConfigureAwait(false);

        var seed = new HashSet<string>();
        foreach (var keptId in plan.KeptNodeIds)
        {
            if (!cells.TryGetValue(keptId, out var cell)) continue;   // never ran (dead branch) — engine re-skips

            var reusable = cell.Status is NodeStatus.Success or NodeStatus.Skipped
                || (cell.Status == NodeStatus.Failure && definition.Edges.Any(e => e.From == keptId && e.SourceHandle == WorkflowHandles.Error));

            if (!reusable)
                throw new RerunUpstreamNotReusableException(
                    $"Cannot reuse upstream node '{keptId}': it did not complete reusably in run {originalRunId} (status {cell.Status}). Re-run from an earlier node, or replay the whole run.");

            seed.Add(keptId);
        }

        return seed;
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

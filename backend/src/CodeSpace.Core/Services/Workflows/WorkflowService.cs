using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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
    private readonly ILogger<WorkflowService> _logger;

    public WorkflowService(CodeSpaceDbContext db, DefinitionValidator validator, INodeRegistry nodeRegistry, Lifecycle.IRunRecordLogger recordLogger, IRunStarter runStarter, IWorkflowRunDispatcher runDispatcher, Engine.IWorkflowResumeService resumeService, ILogger<WorkflowService> logger)
    {
        _db = db;
        _validator = validator;
        _nodeRegistry = nodeRegistry;
        _recordLogger = recordLogger;
        _runStarter = runStarter;
        _runDispatcher = runDispatcher;
        _resumeService = resumeService;
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

        // PostBoy-style dispatch: after the workflow_run row commits in Pending state,
        // atomically transition Pending→Enqueued + hand to the background-job client. Throws
        // if Enqueue fails AND reverts the row; the stuck-run reconciler retries. Out-of-band
        // failure (process dies between SaveChanges + DispatchAsync) leaves the row in Pending
        // → reconciler picks up on its next tick. No double-execution: the CAS at
        // Pending→Enqueued AND the engine's CAS at Enqueued→Running both reject any duplicate
        // dispatch attempts.
        await _runDispatcher.DispatchAsync(runId, cancellationToken).ConfigureAwait(false);

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
            .Where(r => r.Id == originalRunId && r.Workflow.TeamId == teamId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"WorkflowRun {originalRunId} not found in team {teamId}.");

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
            WorkflowId = original.WorkflowId,
            WorkflowVersion = original.WorkflowVersion,
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

        // PostBoy-style dispatch (same as RunManuallyAsync). Atomic Pending→Enqueued CAS +
        // background-job client enqueue. Reconciler picks up if out-of-band failure leaves
        // the row in Pending.
        await _runDispatcher.DispatchAsync(replayRunId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Workflow run replay queued. OriginalRunId={Original} ReplayRunId={Replay} WorkflowId={Workflow} TeamId={Team} SnapshotCount={SnapshotCount}",
            originalRunId, replayRunId, original.WorkflowId, teamId, originalSnapshot.Count);

        return replayRunId;
    }

    public async Task<bool> ApproveRunAsync(Guid runId, Guid teamId, Guid actorUserId, bool approved, string? comment, CancellationToken cancellationToken)
    {
        // Tenancy — the run's workflow must belong to the caller's team (404 conflated with not-yours).
        var exists = await _db.WorkflowRun
            .AnyAsync(r => r.Id == runId && r.Workflow.TeamId == teamId, cancellationToken).ConfigureAwait(false);
        if (!exists) throw new KeyNotFoundException($"WorkflowRun {runId} not found in team {teamId}.");

        // Only act when an Approval wait is actually pending. Approving a run parked on a timer /
        // callback, or one that isn't suspended, is a no-op (the resume CAS would also reject it).
        var hasApprovalWait = await _db.WorkflowRunWait
            .AnyAsync(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending && w.WaitKind == WorkflowWaitKinds.Approval, cancellationToken)
            .ConfigureAwait(false);

        if (!hasApprovalWait) return false;

        var payload = JsonSerializer.Serialize(new
        {
            approved,
            comment = comment ?? string.Empty,
            by = actorUserId.ToString(),
            resumed_at = DateTimeOffset.UtcNow.ToString("o"),
        });

        var resumed = await _resumeService.ResumeAsync(runId, payload, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Run approval. RunId={RunId} TeamId={TeamId} Approved={Approved} By={Actor} Resumed={Resumed}",
            runId, teamId, approved, actorUserId, resumed);

        return resumed;
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
        if (run.Workflow.TeamId != teamId) return null;

        var nodes = await _db.WorkflowRunNode.Where(n => n.RunId == runId).OrderBy(n => n.StartedAt).ToListAsync(cancellationToken).ConfigureAwait(false);
        var normalizedPayload = SafeParseJson(run.RunRequest?.NormalizedPayloadJson);
        var outputs = SafeParseJson(run.OutputsJson);

        // The outstanding wait (if any) — drives the run-detail's resume affordance. Latest
        // pending wins (there is at most one per node; a run parks on one at a time today).
        var pending = await _db.WorkflowRunWait.AsNoTracking()
            .Where(w => w.RunId == runId && w.Status == WorkflowWaitStatuses.Pending)
            .OrderByDescending(w => w.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

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
            Nodes = nodes.Select(MapRunNode).ToList(),
            Outputs = outputs,
            PendingWait = pending == null ? null : new WorkflowRunWaitInfo
            {
                NodeId = pending.NodeId,
                Kind = pending.WaitKind,
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
            IsManual = n.Manifest.IsManual
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

    private static WorkflowRunNodeSummary MapRunNode(WorkflowRunNode n) => new()
    {
        NodeId = n.NodeId,
        IterationKey = n.IterationKey,
        Status = n.Status,
        Inputs = SafeParseJson(n.InputsJson),
        Outputs = SafeParseJson(n.OutputsJson),
        Error = n.Error,
        StartedAt = n.StartedAt,
        CompletedAt = n.CompletedAt
    };

    /// <summary>Delegates to <see cref="WorkflowJsonSafeParse.SafeParse(string?)"/>.</summary>
    private static JsonElement SafeParseJson(string? raw) => WorkflowJsonSafeParse.SafeParse(raw);
}

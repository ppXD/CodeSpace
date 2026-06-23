using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// Stages + dispatches a snapshot run (inline frozen definition, no Workflow row). The flat
/// pipeline: validate → freeze (serialise + hash) → stage (request + run, one tx) → dispatch
/// (existing CAS + Hangfire). The engine's <c>LoadDefinitionAndHashAsync</c> forks on the run's
/// snapshot presence to walk this inline definition with the SAME tamper-check an authored version
/// gets.
/// </summary>
public sealed class RunFromSnapshotStarter : IRunFromSnapshotStarter, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly DefinitionValidator _validator;
    private readonly IRunRecordLogger _recordLogger;
    private readonly IWorkflowRunDispatcher _runDispatcher;
    private readonly IPostCommitActions _postCommit;
    private readonly ILogger<RunFromSnapshotStarter> _logger;

    public RunFromSnapshotStarter(CodeSpaceDbContext db, DefinitionValidator validator, IRunRecordLogger recordLogger, IWorkflowRunDispatcher runDispatcher, IPostCommitActions postCommit, ILogger<RunFromSnapshotStarter> logger)
    {
        _db = db;
        _validator = validator;
        _recordLogger = recordLogger;
        _runDispatcher = runDispatcher;
        _postCommit = postCommit;
        _logger = logger;
    }

    public async Task<Guid> StartFromSnapshotAsync(WorkflowDefinition definition, Guid teamId, Guid actorUserId, string? launchPayloadJson, IReadOnlyList<Guid>? scopeRepositoryIds, string? projectionKind, SessionAssignment? session, CancellationToken cancellationToken)
    {
        EnsureValidDefinition(definition);

        var (definitionJson, definitionHash) = Freeze(definition);
        var payloadJson = NormalizePayload(launchPayloadJson);

        var repositoryIds = scopeRepositoryIds ?? [];
        var projectIds = await DeriveScopeProjectIdsAsync(repositoryIds, teamId, cancellationToken).ConfigureAwait(false);

        var runId = await StageAsync(teamId, actorUserId, definitionJson, definitionHash, payloadJson, WorkflowRunSourceTypes.Snapshot, parentRunId: null, causationRequestId: null, repositoryIds, projectIds, projectionKind, session, cancellationToken).ConfigureAwait(false);

        await _recordLogger.RunQueuedAsync(runId, WorkflowRunSourceTypes.Snapshot, actorUserId, cancellationToken).ConfigureAwait(false);

        // Dispatch AFTER the transaction commits (same discipline as RunManuallyAsync) so a Hangfire
        // worker can't fetch the job before the workflow_run row is visible. The dispatcher's
        // Pending→Enqueued CAS + the engine's Enqueued→Running CAS reject any duplicate; the
        // reconciler backstops a dropped dispatch.
        await _postCommit.RunAfterCommitAsync(ct => _runDispatcher.DispatchAsync(runId, ct), cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Snapshot run queued. TeamId={TeamId} RunId={RunId} Hash={Hash} Nodes={NodeCount} Edges={EdgeCount}",
            teamId, runId, definitionHash, definition.Nodes.Count, definition.Edges.Count);

        return runId;
    }

    /// <summary>
    /// Replay a snapshot/dynamic run: clone its EXACT frozen definition (<paramref name="definitionJson"/> +
    /// <paramref name="definitionHash"/>) onto a NEW snapshot run — no re-validate / re-freeze, so replay
    /// reproduces the definition the original ran byte-for-byte (the engine's snapshot tamper-check passes
    /// because the hash travels with it). This is the snapshot analogue of <see cref="IRunStarter"/>'s authored
    /// replay path: STAGE-ONLY (one tx) + <c>run.queued</c>(Replay), carrying the replay lineage
    /// (<paramref name="parentRunId"/> drives the engine's <c>run.replayed</c>; <paramref name="causationRequestId"/>
    /// links back to the original request). The caller clones the variable snapshot then dispatches — exactly as
    /// the authored-replay path does, so the engine's variable-presence fork takes the replay scope.
    /// </summary>
    public async Task<Guid> StageReplayFromSnapshotAsync(string definitionJson, string definitionHash, Guid teamId, Guid actorUserId, string payloadJson, string sourceType, Guid parentRunId, Guid causationRequestId, IReadOnlyList<Guid> scopeRepositoryIds, IReadOnlyList<Guid> scopeProjectIds, string? projectionKind, SessionAssignment? session, CancellationToken cancellationToken)
    {
        // Replay CLONES the original's scope arrays + projection kind verbatim (point-in-time snapshot — no re-derivation), same as the frozen definition.
        var runId = await StageAsync(teamId, actorUserId, definitionJson, definitionHash, NormalizePayload(payloadJson), sourceType, parentRunId, causationRequestId, scopeRepositoryIds, scopeProjectIds, projectionKind, session, cancellationToken).ConfigureAwait(false);

        await _recordLogger.RunQueuedAsync(runId, sourceType, actorUserId, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Snapshot run fork staged. Source={SourceType} TeamId={TeamId} ForkRunId={RunId} ParentRunId={ParentRunId} Hash={Hash}", sourceType, teamId, runId, parentRunId, definitionHash);

        return runId;
    }

    private void EnsureValidDefinition(WorkflowDefinition definition)
    {
        var result = _validator.Validate(definition);
        if (result.IsValid) return;

        _logger.LogWarning("Snapshot definition rejected by validator. ErrorCount={ErrorCount} Errors={Errors}", result.Errors.Count, string.Join(" | ", result.Errors));

        throw new WorkflowValidationException(result.Errors);
    }

    /// <summary>Serialise the definition with the canonical engine options + compute its tamper hash — the frozen pair carried on the run row.</summary>
    private static (string Json, string Hash) Freeze(WorkflowDefinition definition)
    {
        var json = JsonSerializer.Serialize(definition, WorkflowJson.Options);
        var hash = DefinitionHash.Compute(definition);
        return (json, hash);
    }

    private static string NormalizePayload(string? launchPayloadJson) =>
        string.IsNullOrWhiteSpace(launchPayloadJson) ? "{}" : launchPayloadJson;

    /// <summary>
    /// Derive the run's launch-time SCOPE projects from its repositories via the team's <c>project_repository</c> links
    /// (a repo may be in several projects; soft-deleted links excluded). A point-in-time snapshot — frozen onto the run
    /// so a later repo re-home does not rewrite history. Empty in → empty out (no query).
    /// </summary>
    private async Task<List<Guid>> DeriveScopeProjectIdsAsync(IReadOnlyList<Guid> repositoryIds, Guid teamId, CancellationToken cancellationToken)
    {
        if (repositoryIds.Count == 0) return [];

        return await _db.ProjectRepository
            .Where(pr => pr.TeamId == teamId && pr.DeletedDate == null && repositoryIds.Contains(pr.RepositoryId))
            .Select(pr => pr.ProjectId)
            .Distinct()
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Stage the request + snapshot-run pair in ONE transaction. The request carries source/actor
    /// metadata (WorkflowId left NULL — there is no workflow) plus the optional
    /// <paramref name="causationRequestId"/> linking a replay back to the original request; the run
    /// carries the inline frozen definition + hash with WorkflowId / WorkflowVersion NULL and an
    /// optional <paramref name="parentRunId"/> (set on a replay — the engine emits <c>run.replayed</c>
    /// from it). Status starts Pending — the post-commit dispatch flips it to Enqueued.
    /// </summary>
    private async Task<Guid> StageAsync(Guid teamId, Guid actorUserId, string definitionJson, string definitionHash, string payloadJson, string sourceType, Guid? parentRunId, Guid? causationRequestId, IReadOnlyList<Guid> scopeRepositoryIds, IReadOnlyList<Guid> scopeProjectIds, string? projectionKind, SessionAssignment? session, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        _db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            WorkflowId = null,
            SourceType = sourceType,
            CausationId = causationRequestId,
            ActorType = WorkflowRunActorTypes.User,
            ActorId = actorUserId,
            NormalizedPayloadJson = payloadJson,
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
        });

        _db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId,
            WorkflowId = null,
            WorkflowVersion = null,
            SourceType = sourceType,
            ParentRunId = parentRunId,
            DefinitionSnapshotJson = definitionJson,
            DefinitionSnapshotHash = definitionHash,
            ReleaseHashAtRun = definitionHash,
            TeamId = teamId,
            RunRequestId = requestId,
            ActorId = actorUserId,   // snapshot / task runs are always user-launched (mirrors the request's ActorId)
            ProjectionKind = projectionKind,
            ScopeRepositoryIds = scopeRepositoryIds.ToList(),
            ScopeProjectIds = scopeProjectIds.ToList(),
            SessionId = session?.SessionId,
            SessionTurnIndex = session?.TurnIndex,
            Status = WorkflowRunStatus.Pending,
            CreatedBy = actorUserId,
            LastModifiedBy = actorUserId,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return runId;
    }
}

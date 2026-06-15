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

    public async Task<Guid> StartFromSnapshotAsync(WorkflowDefinition definition, Guid teamId, Guid actorUserId, string? launchPayloadJson, CancellationToken cancellationToken)
    {
        EnsureValidDefinition(definition);

        var (definitionJson, definitionHash) = Freeze(definition);
        var payloadJson = NormalizePayload(launchPayloadJson);

        var runId = await StageAsync(teamId, actorUserId, definitionJson, definitionHash, payloadJson, cancellationToken).ConfigureAwait(false);

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
    /// Stage the request + snapshot-run pair in ONE transaction. The request carries source/actor
    /// metadata (WorkflowId left NULL — there is no workflow); the run carries the inline frozen
    /// definition + hash with WorkflowId / WorkflowVersion NULL. Status starts Pending — the
    /// post-commit dispatch flips it to Enqueued.
    /// </summary>
    private async Task<Guid> StageAsync(Guid teamId, Guid actorUserId, string definitionJson, string definitionHash, string payloadJson, CancellationToken cancellationToken)
    {
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        _db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            WorkflowId = null,
            SourceType = WorkflowRunSourceTypes.Snapshot,
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
            DefinitionSnapshotJson = definitionJson,
            DefinitionSnapshotHash = definitionHash,
            ReleaseHashAtRun = definitionHash,
            TeamId = teamId,
            RunRequestId = requestId,
            Status = WorkflowRunStatus.Pending,
            CreatedBy = actorUserId,
            LastModifiedBy = actorUserId,
        });

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return runId;
    }
}

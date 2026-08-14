using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Events;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// One DB write per call, on a connection of its own, so the audit row commits even when the
/// caller's enclosing transaction later rolls back. This is what makes signature-fail /
/// no-match rejections SURVIVE the controller's 401 — operator debugging is built on these
/// surviving rows.
///
/// <para>The separate connection is the whole mechanism, not an optimisation. Every rejection
/// this class records is reported to its caller by throwing, and the mediator's transactional
/// middleware rolls back on a throw — so an audit written through the request's own DbContext
/// would be erased by the very failure it documents. A row about a failure must not be owned
/// by that failure's transaction.</para>
/// </summary>
public sealed class IngestionAuditor : IIngestionAuditor, IScopedDependency
{
    private readonly DbContextOptions<CodeSpaceDbContext> _options;
    private readonly ILogger<IngestionAuditor> _logger;

    public IngestionAuditor(DbContextOptions<CodeSpaceDbContext> options, ILogger<IngestionAuditor> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task WriteWebhookRejectedAsync(WebhookRejectionContext context, CancellationToken cancellationToken)
    {
        var row = new WorkflowRunRequest
        {
            Id = Guid.NewGuid(),
            TeamId = context.TeamId,
            RepositoryId = context.RepositoryId,
            SourceType = context.SourceType,
            ExternalEventId = context.ExternalEventId,
            // Phase 3.0 — dedup via the per-row uq_wrr_idempotency_key partial unique index.
            // Migration 0024 dropped the global (source_type, external_event_id) unique index
            // to allow multi-activation fan-out on the run-creation path; rejected audit rows
            // still need per-(source, delivery) dedup but don't have an activation to scope by.
            // Use the "rejected:" prefix so this keyspace can never collide with RunStarter's
            // {sourceType}:{deliveryId}:{activationId} form (which has 3+ colons + a GUID
            // suffix and is only emitted for fan-out into specific activations).
            IdempotencyKey = BuildRejectedDedupKey(context.SourceType, context.ExternalEventId),
            ActorType = WorkflowRunActorTypes.Webhook,
            ActorId = null,
            NormalizedPayloadJson = "{}",
            RequestMetadataJson = "{}",
            RawHeadersRedactedJson = context.RawHeadersRedactedJson,
            VerificationResultJson = context.VerificationResultJson,
            Status = WorkflowRunRequestStatus.Rejected,
            Error = $"{context.Reason}: {context.Detail}",
            ReceivedAt = DateTimeOffset.UtcNow,
        };

        await SaveAuditRowAsync(row, context.SourceType, context.ExternalEventId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Synthesise the per-delivery dedup key for rejected audit rows. Null when there's no
    /// delivery id (signature-fail rejection happens BEFORE we parse the body, so no
    /// ExternalEventId); the row is then unique-by-default and we just accept all duplicates
    /// — they're rare and the operator audit value of having both is higher than the cost.
    /// </summary>
    private static string? BuildRejectedDedupKey(string sourceType, string? externalEventId) =>
        externalEventId == null ? null : $"rejected:{sourceType}:{externalEventId}";

    public async Task WriteNoMatchRejectedAsync(NormalizedEvent normalizedEvent, Guid teamId, CancellationToken cancellationToken)
    {
        var sourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}unmatched";
        var row = new WorkflowRunRequest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            // The normalised event names its repository as a required field, so this branch is
            // never the unattributed one — and it is the branch an operator most needs attributed,
            // because "verified, understood, nobody listening" is the one rejection that is not a fault.
            RepositoryId = normalizedEvent.RepositoryId,
            SourceType = sourceType,
            ExternalEventId = normalizedEvent.ProviderEventId,
            IdempotencyKey = BuildRejectedDedupKey(sourceType, normalizedEvent.ProviderEventId),
            ActorType = WorkflowRunActorTypes.Webhook,
            ActorId = null,
            NormalizedPayloadJson = "{}",
            RequestMetadataJson = "{}",
            Status = WorkflowRunRequestStatus.Rejected,
            Error = $"{WorkflowRunRequestRejectionReasons.NoMatchingActivation}: " +
                    $"event {normalizedEvent.GetType().Name} for repository {normalizedEvent.RepositoryId} " +
                    "had no matching enabled activation",
            ReceivedAt = normalizedEvent.OccurredAt,
            VerifiedAt = normalizedEvent.OccurredAt,
            NormalizedAt = normalizedEvent.OccurredAt,
        };

        await SaveAuditRowAsync(row, row.SourceType, row.ExternalEventId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Persist the audit row with PG-23505 (unique violation) tolerance. A retry from the
    /// provider that hits the same failure shouldn't double-insert — the existing row IS
    /// the audit record. Other DB errors propagate so we don't mask real problems.
    /// </summary>
    private async Task SaveAuditRowAsync(WorkflowRunRequest row, string sourceType, string? externalEventId, CancellationToken cancellationToken)
    {
        // Deliberately NOT the request's injected DbContext — see the class remarks. A context
        // built from the shared options opens its own connection, so this insert commits on its
        // own and is untouched by the caller's rollback.
        await using var db = new CodeSpaceDbContext(_options, currentUser: null, botVisibility: null);

        db.WorkflowRunRequest.Add(row);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Ingestion audit: wrote Rejected request {RequestId} (source={SourceType}, externalId={ExternalEventId})",
                row.Id, sourceType, externalEventId ?? "<none>");
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Provider retry hit the same (source_type, external_event_id) — the existing
            // audit row already captures this rejection.
            _logger.LogDebug(
                "Ingestion audit: duplicate retry for (source={SourceType}, externalId={ExternalEventId}); existing row preserved",
                sourceType, externalEventId);
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is Npgsql.PostgresException { SqlState: "23505" };
}

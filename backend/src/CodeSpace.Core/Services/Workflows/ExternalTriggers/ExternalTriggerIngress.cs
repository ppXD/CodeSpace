using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Completion.Exceptions;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// SourceType must equal the activation TypeKey: ActivationTypeFor only translates cron, so any other value fails
/// VerifyActivation with trigger-snapshot-mismatch. Anonymous runs also need the activation's id and authority snapshot.
/// Each call resolves the live activation afresh because ReplaceActivationsAsync rebuilds every row on workflow save.
/// No Idempotency-Key means no deduplication: identical bodies can represent distinct real events, not just retries.
/// </summary>
public sealed class ExternalTriggerIngress(
    CodeSpaceDbContext db,
    IRunStarter runStarter,
    IWorkflowRunDispatcher runDispatcher,
    IPostCommitActions postCommit,
    IIngestionAuditor auditor,
    ILogger<ExternalTriggerIngress> logger) : IExternalTriggerIngress
{
    public async Task<ExternalTriggerOutcome> ReceiveAsync(ExternalTriggerCall call, CancellationToken ct)
    {
        var trigger = await db.WorkflowExternalTrigger.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Slug == call.Slug && t.DeletedDate == null, ct).ConfigureAwait(false);
        if (trigger is null) return Outcome(ExternalTriggerOutcomeKind.NotFound);

        var activations = await db.WorkflowActivation.AsNoTracking().Include(a => a.Workflow)
            .Where(a => a.WorkflowId == trigger.WorkflowId && a.TypeKey == ExternalTriggerSlug.TypeKey
                && a.Enabled && a.DeletedDate == null && a.Workflow.Enabled && a.Workflow.DeletedDate == null)
            .ToListAsync(ct).ConfigureAwait(false);
        if (activations.Count != 1)
        {
            if (activations.Count > 1)
                logger.LogWarning("External trigger workflow {WorkflowId} has {Count} live activations; refusing ambiguous endpoint", trigger.WorkflowId, activations.Count);
            return Outcome(ExternalTriggerOutcomeKind.NotFound);
        }
        var activation = activations[0];

        if (call.PresentedKey is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(ExternalTriggerKeySealer.Digest(call.PresentedKey)), Encoding.ASCII.GetBytes(trigger.KeyDigest)))
            return Outcome(ExternalTriggerOutcomeKind.Unauthorized);

        if (call.IdempotencyRejected)
            return Outcome(ExternalTriggerOutcomeKind.InvalidPayload,
                errors: [$"Idempotency-Key must be at most {ExternalTriggerIdempotency.MaxLength} characters from [A-Za-z0-9._~-]"]);

        JsonDocument body;
        try { body = JsonDocument.Parse(call.RawBody); }
        catch (JsonException) { return InvalidBody(); }
        using (body)
        {
            if (body.RootElement.ValueKind != JsonValueKind.Object) return InvalidBody();
            using var config = JsonDocument.Parse(activation.ConfigJson);
            using var fallback = JsonDocument.Parse("""{"type":"object"}""");
            var schema = config.RootElement.ValueKind == JsonValueKind.Object
                && config.RootElement.TryGetProperty("inputSchema", out var declared) && declared.ValueKind == JsonValueKind.Object
                ? declared : fallback.RootElement;
            var errors = ExternalTriggerPayloadValidator.Validate(body.RootElement, schema);
            if (errors.Count != 0) return Outcome(ExternalTriggerOutcomeKind.InvalidPayload, errors: errors);

            var envelope = new RunSourceEnvelope
            {
                TeamId = activation.Workflow.TeamId,
                WorkflowId = activation.WorkflowId,
                WorkflowVersion = activation.Workflow.LatestVersion,
                SourceType = ExternalTriggerSlug.TypeKey,
                ActorType = WorkflowRunActorTypes.Webhook,
                ActorId = null,
                CreatedBy = SystemUsers.SeederId,
                ActivationId = activation.Id,
                ActivationSnapshotJson = ActivationAuthoritySnapshot.Serialize(activation),
                NormalizedPayloadJson = body.RootElement.GetRawText(),
                IdempotencyKey = call.IdempotencyKey is null ? null : ExternalTriggerIdempotency.KeyFor(call.IdempotencyKey, activation.Id),
            };

            Guid runId;
            try { runId = await runStarter.StartAsync(envelope, ct).ConfigureAwait(false); }
            catch (AgentAuthorityDeniedException ex)
            {
                await AuditRefusalAsync(activation, call, ex.Code, ex.Reason, ct).ConfigureAwait(false);
                return Outcome(ExternalTriggerOutcomeKind.Accepted);
            }
            catch (CompletionAdmissionRefusedException ex)
            {
                await AuditRefusalAsync(activation, call, WorkflowRunRequestRejectionReasons.CompletionAdmissionRefused, ex.Message, ct).ConfigureAwait(false);
                return Outcome(ExternalTriggerOutcomeKind.Accepted);
            }

            if (runId == Guid.Empty)
            {
                var existing = envelope.IdempotencyKey is null ? null : await db.WorkflowRun.AsNoTracking()
                    .Where(r => r.RunRequest.IdempotencyKey == envelope.IdempotencyKey)
                    .Select(r => (Guid?)r.Id).SingleOrDefaultAsync(ct).ConfigureAwait(false);
                if (existing.HasValue) return Outcome(ExternalTriggerOutcomeKind.Deduplicated, existing.Value);
                logger.LogWarning("External trigger {Slug} was deduplicated but its run could not be found", call.Slug);
                return Outcome(ExternalTriggerOutcomeKind.Accepted);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await postCommit.RunAfterCommitAsync(token => runDispatcher.DispatchAsync(runId, token), ct).ConfigureAwait(false);
            return Outcome(ExternalTriggerOutcomeKind.Accepted, runId);
        }
    }

    private async Task AuditRefusalAsync(WorkflowActivation activation, ExternalTriggerCall call, string reason, string detail, CancellationToken ct)
    {
        logger.LogWarning("External trigger activation {ActivationId} workflow {WorkflowId} refused: {Reason} {Detail}", activation.Id, activation.WorkflowId, reason, detail);
        await auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = activation.Workflow.TeamId,
            SourceType = ExternalTriggerSlug.TypeKey,
            ExternalEventId = call.IdempotencyKey,
            Reason = reason,
            Detail = $"Activation {activation.Id} workflow {activation.WorkflowId}: {detail}",
            DedupKey = $"rejected:{call.Slug}:{call.IdempotencyKey}",
        }, ct).ConfigureAwait(false);
    }

    private static ExternalTriggerOutcome InvalidBody() => Outcome(ExternalTriggerOutcomeKind.InvalidPayload, errors: ["$: body must be a JSON object"]);

    private static ExternalTriggerOutcome Outcome(ExternalTriggerOutcomeKind kind, Guid runId = default, IReadOnlyList<string>? errors = null) => new(kind, runId, errors ?? []);
}

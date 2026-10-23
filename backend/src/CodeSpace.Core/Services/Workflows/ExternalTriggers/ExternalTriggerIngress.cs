using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents.Authority.Exceptions;
using CodeSpace.Core.Services.Completion.Exceptions;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.ExternalTriggers;

/// <summary>
/// SourceType must equal the activation TypeKey: ActivationTypeFor only translates cron, so any other value fails
/// VerifyActivation with trigger-snapshot-mismatch. Anonymous runs also need the activation's id and authority snapshot.
/// Each call resolves the live activation afresh because ReplaceActivationsAsync rebuilds every row on workflow save.
/// The latest version's node id binds the credential to its original node; replacing a node must not revive its old key.
/// Its node config also supplies the input contract so identity and validation use the same definition version.
/// No Idempotency-Key means no deduplication: identical bodies can represent distinct real events, not just retries.
/// </summary>
public sealed class ExternalTriggerIngress(
    CodeSpaceDbContext db,
    IRunStarter runStarter,
    IWorkflowRunDispatcher runDispatcher,
    IPostCommitActions postCommit,
    IIngestionAuditor auditor,
    ExternalTriggerRunAdmission admission,
    ILogger<ExternalTriggerIngress> logger) : IExternalTriggerIngress
{
    public async Task<ExternalTriggerVerification> VerifyAsync(string slug, string? presentedKey, CancellationToken ct)
    {
        var trigger = await db.WorkflowExternalTrigger.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Slug == slug && t.DeletedDate == null, ct).ConfigureAwait(false);
        if (trigger is null) return Verification(ExternalTriggerOutcomeKind.NotFound);

        var activations = await db.WorkflowActivation.AsNoTracking().Include(a => a.Workflow)
            .Where(a => a.WorkflowId == trigger.WorkflowId && a.TypeKey == ExternalTriggerSlug.TypeKey
                && a.Enabled && a.DeletedDate == null && a.Workflow.Enabled && a.Workflow.DeletedDate == null)
            .ToListAsync(ct).ConfigureAwait(false);
        if (activations.Count != 1)
        {
            if (activations.Count > 1)
                logger.LogWarning("External trigger workflow {WorkflowId} has {Count} live activations; refusing ambiguous endpoint", trigger.WorkflowId, activations.Count);
            return Verification(ExternalTriggerOutcomeKind.NotFound);
        }
        var activation = activations[0];

        var definitionJson = await db.WorkflowVersion.AsNoTracking()
            .Where(v => v.WorkflowId == activation.WorkflowId && v.Version == activation.Workflow.LatestVersion)
            .Select(v => v.DefinitionJson).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (definitionJson is null) return Verification(ExternalTriggerOutcomeKind.NotFound);
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(definitionJson, WorkflowJson.Options);
        var nodes = definition?.Nodes.Where(n => n.TypeKey == ExternalTriggerSlug.TypeKey).ToList();
        if (nodes is not { Count: 1 } || nodes[0].Id != trigger.NodeId) return Verification(ExternalTriggerOutcomeKind.NotFound);
        var config = nodes[0].Config;

        if (presentedKey is null || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(ExternalTriggerKeySealer.Digest(presentedKey)), Encoding.ASCII.GetBytes(trigger.KeyDigest)))
            return Verification(ExternalTriggerOutcomeKind.Unauthorized);

        var schema = config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("inputSchema", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared : default;

        return new ExternalTriggerVerification(
            ExternalTriggerOutcomeKind.Accepted,
            trigger.Id,
            activation.Id,
            activation.WorkflowId,
            activation.Workflow.TeamId,
            activation.Workflow.LatestVersion,
            ActivationAuthoritySnapshot.Serialize(activation),
            schema);
    }

    public async Task<ExternalTriggerOutcome> ReceiveAsync(ExternalTriggerCall call, CancellationToken ct)
    {
        var verification = await VerifyAsync(call.Slug, call.PresentedKey, ct).ConfigureAwait(false);
        if (verification.Kind != ExternalTriggerOutcomeKind.Accepted)
            return Outcome(verification.Kind);

        if (call.IdempotencyRejected)
            return Outcome(ExternalTriggerOutcomeKind.InvalidPayload,
                errors: [$"Idempotency-Key must be at most {ExternalTriggerIdempotency.MaxLength} characters from [A-Za-z0-9._~-]"]);

        JsonDocument body;
        try { body = JsonDocument.Parse(call.RawBody); }
        catch (JsonException) { return InvalidBody(); }
        using (body)
        {
            if (body.RootElement.ValueKind != JsonValueKind.Object) return InvalidBody();
            using var fallback = JsonDocument.Parse("""{"type":"object"}""");
            var schema = verification.InputSchema.ValueKind == JsonValueKind.Object ? verification.InputSchema : fallback.RootElement;
            var errors = ExternalTriggerPayloadValidator.Validate(body.RootElement, schema);
            if (errors.Count != 0) return Outcome(ExternalTriggerOutcomeKind.InvalidPayload, errors: errors);

            var envelope = new RunSourceEnvelope
            {
                TeamId = verification.TeamId,
                WorkflowId = verification.WorkflowId,
                WorkflowVersion = verification.WorkflowVersion,
                SourceType = ExternalTriggerSlug.TypeKey,
                ActorType = WorkflowRunActorTypes.Webhook,
                ActorId = null,
                CreatedBy = SystemUsers.SeederId,
                ActivationId = verification.ActivationId,
                ActivationSnapshotJson = verification.ActivationSnapshotJson,
                NormalizedPayloadJson = body.RootElement.GetRawText(),
                IdempotencyKey = call.IdempotencyKey is null ? null : ExternalTriggerIdempotency.KeyFor(call.IdempotencyKey, verification.TriggerId),
            };

            if (!admission.TryAcquire(verification.TriggerId))
            {
                await AuditRefusalAsync(verification, call, WorkflowRunRequestRejectionReasons.TriggerRateLimited, "per-trigger run admission refused", ct).ConfigureAwait(false);
                return Outcome(ExternalTriggerOutcomeKind.RateLimited);
            }

            Guid runId;
            try { runId = await runStarter.StartAsync(envelope, ct).ConfigureAwait(false); }
            catch (AgentAuthorityDeniedException ex)
            {
                await AuditRefusalAsync(verification, call, ex.Code, ex.Reason, ct).ConfigureAwait(false);
                return Outcome(ExternalTriggerOutcomeKind.Accepted);
            }
            catch (CompletionAdmissionRefusedException ex)
            {
                await AuditRefusalAsync(verification, call, WorkflowRunRequestRejectionReasons.CompletionAdmissionRefused, ex.Message, ct).ConfigureAwait(false);
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

    private async Task AuditRefusalAsync(ExternalTriggerVerification verification, ExternalTriggerCall call, string reason, string detail, CancellationToken ct)
    {
        logger.LogWarning("External trigger activation {ActivationId} workflow {WorkflowId} refused: {Reason} {Detail}", verification.ActivationId, verification.WorkflowId, reason, detail);
        await auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = verification.TeamId,
            SourceType = ExternalTriggerSlug.TypeKey,
            ExternalEventId = call.IdempotencyKey,
            Reason = reason,
            Detail = $"Activation {verification.ActivationId} workflow {verification.WorkflowId}: {detail}",
            DedupKey = BuildRefusalWindowKey(verification.TriggerId, reason, DateTimeOffset.UtcNow),
        }, ct).ConfigureAwait(false);
    }

    private static string BuildRefusalWindowKey(Guid triggerId, string reason, DateTimeOffset now) =>
        $"refused:{triggerId:N}:{reason}:{now.UtcTicks / WebhookIngestionService.RefusalAuditWindow.Ticks}";

    private static ExternalTriggerVerification Verification(ExternalTriggerOutcomeKind kind) =>
        new(kind, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, 0, string.Empty, default);

    private static ExternalTriggerOutcome InvalidBody() => Outcome(ExternalTriggerOutcomeKind.InvalidPayload, errors: ["$: body must be a JSON object"]);

    private static ExternalTriggerOutcome Outcome(ExternalTriggerOutcomeKind kind, Guid runId = default, IReadOnlyList<string>? errors = null) => new(kind, runId, errors ?? []);
}

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
    ExternalTriggerVerificationContext verificationContext,
    TransactionCompletionCallbacks completion,
    ILogger<ExternalTriggerIngress> logger) : IExternalTriggerIngress
{
    public async Task<ExternalTriggerVerification> VerifyAsync(Guid triggerId, string? presentedKey, CancellationToken ct)
    {
        verificationContext.Ticket = null;
        var trigger = await db.WorkflowExternalTrigger.AsNoTracking()
            .Where(t => t.Id == triggerId && t.DeletedDate == null)
            .Select(t => new ExternalTriggerVerificationTicket(t.Id, t.TeamId, t.WorkflowId, t.NodeId, t.KeyDigest))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (trigger is null || !KeyMatches(presentedKey, trigger.KeyDigest))
            return Verification(ExternalTriggerOutcomeKind.Unauthorized);

        verificationContext.Ticket = trigger;
        return Verification(ExternalTriggerOutcomeKind.Accepted);
    }

    private async Task<ExternalTriggerVerification> ResolveAsync(ExternalTriggerVerificationTicket ticket, CancellationToken ct)
    {
        var trigger = await db.WorkflowExternalTrigger.AsNoTracking()
            .Where(t => t.Id == ticket.TriggerId && t.DeletedDate == null)
            .Select(t => new ExternalTriggerVerificationTicket(t.Id, t.TeamId, t.WorkflowId, t.NodeId, t.KeyDigest))
            .SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (trigger is null || trigger != ticket) return Verification(ExternalTriggerOutcomeKind.Unauthorized);

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

        var schema = config.ValueKind == JsonValueKind.Object
            && config.TryGetProperty("inputSchema", out var declared) && declared.ValueKind == JsonValueKind.Object
            ? declared : default;

        return new ExternalTriggerVerification(
            ExternalTriggerOutcomeKind.Accepted,
            trigger.TriggerId,
            activation.Id,
            activation.WorkflowId,
            activation.Workflow.TeamId,
            activation.Workflow.LatestVersion,
            ActivationAuthoritySnapshot.Serialize(activation),
            schema);
    }

    public async Task<ExternalTriggerOutcome> ReceiveAsync(ExternalTriggerCall call, CancellationToken ct)
    {
        var ticket = verificationContext.Ticket;
        verificationContext.Ticket = null;
        if (ticket is null || ticket.TriggerId != call.TriggerId || !KeyMatches(call.PresentedKey, ticket.KeyDigest))
        {
            var authentication = await VerifyAsync(call.TriggerId, call.PresentedKey, ct).ConfigureAwait(false);
            ticket = verificationContext.Ticket;
            verificationContext.Ticket = null;
            if (authentication.Kind != ExternalTriggerOutcomeKind.Accepted || ticket is null)
                return Outcome(ExternalTriggerOutcomeKind.Unauthorized);
        }
        if (call.IdempotencyRejected)
            return Outcome(ExternalTriggerOutcomeKind.InvalidPayload,
                errors: [$"Idempotency-Key must be at most {ExternalTriggerIdempotency.MaxLength} characters from [A-Za-z0-9._~-]"]);

        JsonDocument? body;
        try { body = call.ParsedBody.HasValue ? null : JsonDocument.Parse(call.RawBody); }
        catch (JsonException) { return InvalidBody(); }
        using (body)
        {
            var payload = call.ParsedBody ?? body!.RootElement;
            if (payload.ValueKind != JsonValueKind.Object) return InvalidBody();
            var errors = ExternalTriggerPayloadValidator.ValidatePayload(payload);
            if (errors.Count != 0) return Outcome(ExternalTriggerOutcomeKind.InvalidPayload, errors: errors);

            if (db.Database.CurrentTransaction is not null)
                return await ReceiveInTransactionAsync(call, ticket, payload, ct).ConfigureAwait(false);

            var checkpoint = postCommit.CreateCheckpoint();
            await using var transaction = await ScopedTransaction.OwnOrJoinAsync(db.Database, ct).ConfigureAwait(false);
            ExternalTriggerOutcome outcome;
            try
            {
                outcome = await ReceiveInTransactionAsync(call, ticket, payload, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                try { await db.Database.CurrentTransaction!.RollbackAsync(CancellationToken.None).ConfigureAwait(false); }
                finally { postCommit.RollbackTo(checkpoint); completion.RunAfterRollback(); }
                throw;
            }
            await transaction.DisposeAsync().ConfigureAwait(false);
            await completion.RunAfterCommitAsync(CancellationToken.None).ConfigureAwait(false);
            await postCommit.RunAllAsync(CancellationToken.None).ConfigureAwait(false);
            return outcome;
        }
    }

    private async Task<ExternalTriggerOutcome> ReceiveInTransactionAsync(ExternalTriggerCall call, ExternalTriggerVerificationTicket ticket, JsonElement payload, CancellationToken ct)
    {
            // Saves and ingestion take workflow before trigger; body reads never hold these locks.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM workflow WHERE id = {ticket.WorkflowId} FOR UPDATE", ct).ConfigureAwait(false);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM workflow_external_trigger WHERE id = {ticket.TriggerId} FOR UPDATE", ct).ConfigureAwait(false);
            var current = await db.WorkflowExternalTrigger.AsNoTracking()
                .Where(t => t.Id == ticket.TriggerId && t.DeletedDate == null)
                .Select(t => new ExternalTriggerVerificationTicket(t.Id, t.TeamId, t.WorkflowId, t.NodeId, t.KeyDigest))
                .SingleOrDefaultAsync(ct).ConfigureAwait(false);
            if (current != ticket) return Outcome(ExternalTriggerOutcomeKind.Unauthorized);

            var idempotencyKey = call.IdempotencyKey is null ? null : ExternalTriggerIdempotency.KeyFor(call.IdempotencyKey, ticket.TriggerId);
            var existing = await FindReplayAsync(idempotencyKey, payload, ct).ConfigureAwait(false);
            if (existing is not null) return existing;

            var verification = await ResolveAsync(ticket, ct).ConfigureAwait(false);
            if (verification.Kind != ExternalTriggerOutcomeKind.Accepted) return Outcome(verification.Kind);
            using var fallback = JsonDocument.Parse("""{"type":"object"}""");
            var schema = verification.InputSchema.ValueKind == JsonValueKind.Object ? verification.InputSchema : fallback.RootElement;
            var errors = ExternalTriggerPayloadValidator.Validate(payload, schema);
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
                NormalizedPayloadJson = payload.GetRawText(),
                IdempotencyKey = idempotencyKey,
            };

            var reservation = admission.Reserve(verification.TriggerId);
            if (reservation is null)
            {
                await AuditRefusalAsync(verification, call, WorkflowRunRequestRejectionReasons.TriggerRateLimited, "per-trigger run admission refused", ct).ConfigureAwait(false);
                return Outcome(ExternalTriggerOutcomeKind.RateLimited);
            }

            Guid runId;
            try { runId = await runStarter.StartAsync(envelope, ct).ConfigureAwait(false); }
            catch (AgentAuthorityDeniedException ex)
            {
                reservation.Release();
                await AuditRefusalAsync(verification, call, ex.Code, ex.Reason, ct).ConfigureAwait(false);
                return Outcome(ExternalTriggerOutcomeKind.Forbidden, errors: [ex.Message]);
            }
            catch (CompletionAdmissionRefusedException ex)
            {
                reservation.Release();
                await AuditRefusalAsync(verification, call, WorkflowRunRequestRejectionReasons.CompletionAdmissionRefused, ex.Message, ct).ConfigureAwait(false);
                return Outcome(ExternalTriggerOutcomeKind.Unprocessable, errors: [ex.Message]);
            }
            catch { reservation.Release(); throw; }

            if (runId == Guid.Empty)
            {
                reservation.Release();
                return await FindReplayAsync(idempotencyKey, payload, ct).ConfigureAwait(false)
                    ?? throw new ExternalTriggerIdempotencyIntegrityException("External trigger duplicate has no durable run.");
            }

            if (db.Database.CurrentTransaction is null) reservation.Commit();
            else
            {
                completion.RegisterAfterCommit(_ => { reservation.Commit(); return Task.CompletedTask; });
                completion.RegisterAfterRollback(reservation.Release);
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await postCommit.RunAfterCommitAsync(token => runDispatcher.DispatchAsync(runId, token), ct).ConfigureAwait(false);
            return Outcome(ExternalTriggerOutcomeKind.Accepted, runId);
    }

    private async Task<ExternalTriggerOutcome?> FindReplayAsync(string? key, JsonElement payload, CancellationToken ct)
    {
        if (key is null) return null;
        var request = await db.WorkflowRunRequest.AsNoTracking().Where(r => r.IdempotencyKey == key)
            .Select(r => new { r.Id, r.NormalizedPayloadJson }).SingleOrDefaultAsync(ct).ConfigureAwait(false);
        if (request is null) return null;
        var runId = await db.WorkflowRun.AsNoTracking().Where(r => r.RunRequestId == request.Id)
            .Select(r => (Guid?)r.Id).SingleOrDefaultAsync(ct).ConfigureAwait(false)
            ?? throw new ExternalTriggerIdempotencyIntegrityException($"External trigger accepted request {request.Id} has no durable run.");
        using var stored = JsonDocument.Parse(request.NormalizedPayloadJson);
        return ExternalTriggerJsonEquality.AreEquivalent(payload, stored.RootElement)
            ? Outcome(ExternalTriggerOutcomeKind.Deduplicated, runId)
            : new ExternalTriggerOutcome(ExternalTriggerOutcomeKind.Unprocessable, Guid.Empty, [], CodeSpace.Messages.Failures.FailureCodes.ExternalTriggerIdempotencyConflict);
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

    private static bool KeyMatches(string? presentedKey, string digest) => presentedKey is not null
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(ExternalTriggerKeySealer.Digest(presentedKey)), Encoding.ASCII.GetBytes(digest));

    private static ExternalTriggerOutcome InvalidBody() => Outcome(ExternalTriggerOutcomeKind.InvalidPayload, errors: ["$: body must be a JSON object"]);

    private static ExternalTriggerOutcome Outcome(ExternalTriggerOutcomeKind kind, Guid runId = default, IReadOnlyList<string>? errors = null) => new(kind, runId, errors ?? []);
}

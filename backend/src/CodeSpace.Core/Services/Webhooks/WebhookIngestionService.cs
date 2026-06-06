using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Events;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks;

public sealed class WebhookIngestionService : IWebhookIngestionService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IPayloadEncryptor _encryptor;
    private readonly IMediator _mediator;
    private readonly IIngestionAuditor _auditor;
    private readonly ILogger<WebhookIngestionService> _logger;

    public WebhookIngestionService(CodeSpaceDbContext db, IProviderRegistry registry, IPayloadEncryptor encryptor, IMediator mediator, IIngestionAuditor auditor, ILogger<WebhookIngestionService> logger)
    {
        _db = db;
        _registry = registry;
        _encryptor = encryptor;
        _mediator = mediator;
        _auditor = auditor;
        _logger = logger;
    }

    public async Task IngestAsync(Guid webhookId, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var webhook = await LoadWebhookAsync(webhookId, cancellationToken).ConfigureAwait(false);
        await EnsureWebhookActiveOrAuditAsync(webhook, headers, cancellationToken).ConfigureAwait(false);

        var providerKind = webhook.Repository.ProviderInstance.Provider;
        var verifier = _registry.Require<IWebhookSignatureVerifier>(providerKind);
        var normalizer = _registry.Require<IWebhookEventNormalizer>(providerKind);
        var secret = _encryptor.Decrypt(webhook.SecretEnc);

        await VerifySignatureOrAuditAsync(verifier, body, headers, secret, webhook, cancellationToken).ConfigureAwait(false);

        webhook.LastReceivedDate = DateTimeOffset.UtcNow;

        await PublishNormalizedEventAsync(normalizer, webhook, body, headers, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RepositoryWebhook> LoadWebhookAsync(Guid webhookId, CancellationToken cancellationToken)
    {
        var webhook = await _db.RepositoryWebhook
            .Include(w => w.Repository).ThenInclude(r => r.ProviderInstance)
            .SingleOrDefaultAsync(w => w.Id == webhookId, cancellationToken).ConfigureAwait(false);

        if (webhook == null) throw new InvalidOperationException($"Webhook {webhookId} not found");

        return webhook;
    }

    private async Task EnsureWebhookActiveOrAuditAsync(RepositoryWebhook webhook, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (webhook.Active) return;

        // Operator disabled this webhook but the provider is still delivering. Write a Rejected
        // audit row so the "why didn't my webhook fire" view shows the operator-disabled state,
        // then throw to short-circuit the rest of ingestion.
        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = webhook.Repository.TeamId,
            Reason = WorkflowRunRequestRejectionReasons.WebhookInactive,
            Detail = $"webhook {webhook.Id} is configured as inactive",
            SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}{webhook.Repository.ProviderInstance.Provider.ToString().ToLowerInvariant()}",
            ExternalEventId = null,    // pre-classification — we never read the body for an inactive webhook
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);

        throw new InvalidOperationException($"Webhook {webhook.Id} is inactive");
    }

    private async Task VerifySignatureOrAuditAsync(IWebhookSignatureVerifier verifier, string body, IReadOnlyDictionary<string, string> headers, string secret, RepositoryWebhook webhook, CancellationToken cancellationToken)
    {
        if (verifier.VerifySignature(body, headers, secret)) return;

        _logger.LogWarning("Webhook {WebhookId} signature verification failed", webhook.Id);

        // Capture the failed verification as a Rejected request row so the operator can see
        // "delivery N rejected for invalid signature" instead of guessing from a 401 in nginx
        // logs. Write happens BEFORE the throw so the controller's exception filter doesn't
        // suppress the audit.
        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = webhook.Repository.TeamId,
            Reason = WorkflowRunRequestRejectionReasons.SignatureInvalid,
            Detail = $"signature did not validate for webhook {webhook.Id}",
            SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}{webhook.Repository.ProviderInstance.Provider.ToString().ToLowerInvariant()}",
            ExternalEventId = null,    // body is untrusted — we don't extract delivery id pre-verification
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
            VerificationResultJson = JsonSerializer.Serialize(new { validated = false, verifier_class = verifier.GetType().Name }),
        }, cancellationToken).ConfigureAwait(false);

        throw new UnauthorizedAccessException("Webhook signature verification failed");
    }

    private async Task PublishNormalizedEventAsync(IWebhookEventNormalizer normalizer, RepositoryWebhook webhook, string body, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        NormalizedEvent? normalizedEvent;

        try
        {
            normalizedEvent = normalizer.Normalize(webhook.RepositoryId, body, headers);
        }
        // Every way a normalizer can fail to read an untrusted body: JsonException (not JSON),
        // KeyNotFoundException (missing GetProperty), InvalidOperationException (GetString/GetBoolean
        // on the wrong ValueKind), FormatException/OverflowException (GetInt32 on a JSON number that
        // isn't a valid Int32 — a float, scientific notation, or an out-of-range value). All are
        // malformed-payload symptoms, not system faults, so they audit + return 200 rather than
        // escaping as a 500 (which makes providers retry-storm / GitLab auto-disable the webhook).
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            await AuditMalformedPayloadAsync(webhook, headers, ex, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (normalizedEvent == null)
        {
            await AuditEventNotMappedAsync(webhook, headers, cancellationToken).ConfigureAwait(false);
            return;
        }

        await _mediator.Publish(normalizedEvent, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Signature passed but the body couldn't be parsed into the expected shape (non-JSON, or
    /// missing / mistyped fields a normalizer requires — a provider API change, a truncated
    /// delivery, a hand-crafted request). We return normally so the controller responds 200:
    /// providers retry-storm on 5xx and GitLab auto-disables a webhook after repeated failures.
    /// A Rejected row is recorded so the operator sees "delivery arrived but was malformed"
    /// instead of guessing from a 500. Only the exception TYPE is stored — its message can echo
    /// payload fragments we don't want in the audit trail.
    /// </summary>
    private async Task AuditMalformedPayloadAsync(RepositoryWebhook webhook, IReadOnlyDictionary<string, string> headers, Exception error, CancellationToken cancellationToken)
    {
        _logger.LogWarning(error, "Webhook {WebhookId} payload could not be parsed into a tracked event", webhook.Id);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = webhook.Repository.TeamId,
            Reason = WorkflowRunRequestRejectionReasons.MalformedPayload,
            Detail = $"normalizer could not parse the payload for provider {webhook.Repository.ProviderInstance.Provider}: {error.GetType().Name}",
            SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}{webhook.Repository.ProviderInstance.Provider.ToString().ToLowerInvariant()}",
            ExternalEventId = TryExtractDeliveryId(headers),    // sig already passed, headers are trusted
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Payload parsed fine but the normalizer returned null — a valid provider event we don't
    /// track (e.g. a "deployment" event for a repo subscribed only to PRs). Audited so operators
    /// can answer "I sent X but nothing happened" without reading server logs.
    /// </summary>
    private async Task AuditEventNotMappedAsync(RepositoryWebhook webhook, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Webhook {WebhookId} payload not mapped to a tracked event type", webhook.Id);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = webhook.Repository.TeamId,
            Reason = WorkflowRunRequestRejectionReasons.EventNotMapped,
            Detail = $"normalizer for provider {webhook.Repository.ProviderInstance.Provider} returned null for this payload",
            SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}{webhook.Repository.ProviderInstance.Provider.ToString().ToLowerInvariant()}",
            ExternalEventId = TryExtractDeliveryId(headers),    // sig already passed, headers are trusted
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serialise the request headers with secret/auth values stripped. The audit row stores
    /// header NAMES (operators want to see "Authorization was present" without leaking the
    /// token); add header values only for explicitly safe ones (Content-Type, User-Agent).
    /// </summary>
    private static string SerializeRedactedHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var safeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Content-Type", "User-Agent", "X-GitHub-Event", "X-GitHub-Delivery", "X-Gitlab-Event", "X-Gitlab-Event-UUID" };
        var redacted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers)
            redacted[name] = safeNames.Contains(name) ? value : "[REDACTED]";
        return JsonSerializer.Serialize(redacted);
    }

    /// <summary>
    /// Best-effort delivery id extraction from common provider headers. Returns null if no
    /// known header is present — the caller's audit row leaves <c>external_event_id</c> null
    /// in that case (provider doesn't dedup retries, so neither do we).
    /// </summary>
    private static string? TryExtractDeliveryId(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var headerName in new[] { "X-GitHub-Delivery", "X-Gitlab-Event-UUID" })
        {
            if (Providers.WebhookHeaderLookup.TryFind(headers, headerName, out var value)) return value;
        }
        return null;
    }
}

using System.Text.Json;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// Every way a delivery can be refused, and the row each one leaves behind. Split out because these
/// are one concern — what the operator reads when they ask "why didn't my webhook fire" — and both
/// ingestion pipelines write through them unchanged.
/// </summary>
public sealed partial class WebhookIngestionService
{
    /// <summary>How long one unbound repository's refusals collapse into a single audit row. See <see cref="AuditRepositoryNotBoundAsync"/>.</summary>
    private static readonly TimeSpan UnboundAuditWindow = TimeSpan.FromDays(1);

    private async Task EnsureActiveOrAuditAsync(bool active, IngestionSubject subject, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (active) return;

        // Operator disabled this webhook but the provider is still delivering. Write a Rejected
        // audit row so the "why didn't my webhook fire" view shows the operator-disabled state,
        // then throw to short-circuit the rest of ingestion.
        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.WebhookInactive,
            Detail = $"webhook {subject.WebhookId} is configured as inactive",
            SourceType = BuildSourceType(subject),
            ExternalEventId = null,    // pre-classification — we never read the body for an inactive webhook
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);

        throw new InvalidOperationException($"Webhook {subject.WebhookId} is inactive");
    }

    /// <summary>
    /// A hook the connection has moved off must stop being a way in. <c>Active</c> does not cover
    /// this: a retired row is still <c>active = true</c>, because nobody switched it off — the scope
    /// switch retired it, and the switch's whole promise is that the outgoing mode stops delivering
    /// before the incoming one starts. Without this gate that promise holds only for the hooks the
    /// provider let us delete, and the ones it did not keep starting runs in a mode this connection
    /// has left.
    /// </summary>
    private async Task EnsureNotRetiredOrAuditAsync(RepositoryWebhookRegistrationStatus status, IngestionSubject subject, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (WebhookRegistrationLifecycle.InService.Contains(status)) return;

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.WebhookRetired,
            Detail = $"webhook {subject.WebhookId} was retired ({status}) and no longer accepts deliveries",
            SourceType = BuildSourceType(subject),
            ExternalEventId = null,    // pre-classification — a retired hook's body is never read
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);

        throw new InvalidOperationException($"Webhook {subject.WebhookId} was retired ({status})");
    }

    private async Task VerifySignatureOrAuditAsync(IWebhookSignatureVerifier verifier, string body, IReadOnlyDictionary<string, string> headers, string secret, IngestionSubject subject, CancellationToken cancellationToken)
    {
        if (verifier.VerifySignature(body, headers, secret)) return;

        _logger.LogWarning("Webhook {WebhookId} signature verification failed", subject.WebhookId);

        // Capture the failed verification as a Rejected request row so the operator can see
        // "delivery N rejected for invalid signature" instead of guessing from a 401 in nginx
        // logs. Write happens BEFORE the throw so the controller's exception filter doesn't
        // suppress the audit.
        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.SignatureInvalid,
            Detail = $"signature did not validate for webhook {subject.WebhookId}",
            SourceType = BuildSourceType(subject),
            ExternalEventId = null,    // body is untrusted — we don't extract delivery id pre-verification
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
            VerificationResultJson = JsonSerializer.Serialize(new { validated = false, verifier_class = verifier.GetType().Name }),
        }, cancellationToken).ConfigureAwait(false);

        throw new UnauthorizedAccessException("Webhook signature verification failed");
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
    private async Task AuditMalformedPayloadAsync(IngestionSubject subject, IReadOnlyDictionary<string, string> headers, Exception error, CancellationToken cancellationToken)
    {
        _logger.LogWarning(error, "Webhook {WebhookId} payload could not be parsed into a tracked event", subject.WebhookId);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.MalformedPayload,
            Detail = $"normalizer could not parse the payload for provider {subject.Provider}: {error.GetType().Name}",
            SourceType = BuildSourceType(subject),
            ExternalEventId = TryExtractDeliveryId(headers),    // sig already passed, headers are trusted
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Payload parsed fine but the normalizer returned null — a valid provider event we don't
    /// track (e.g. a "deployment" event for a repo subscribed only to PRs). Audited so operators
    /// can answer "I sent X but nothing happened" without reading server logs.
    /// </summary>
    private async Task AuditEventNotMappedAsync(IngestionSubject subject, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Webhook {WebhookId} payload not mapped to a tracked event type", subject.WebhookId);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.EventNotMapped,
            Detail = $"normalizer for provider {subject.Provider} returned null for this payload",
            SourceType = BuildSourceType(subject),
            ExternalEventId = TryExtractDeliveryId(headers),    // sig already passed, headers are trusted
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The delivery was signed by our own hook, named a real repository, and that repository is not
    /// one we have bound. This is the ordinary case for a group hook, not a fault — the hook covers
    /// every project in the group and we asked for a handful of them — so it is dropped and
    /// recorded rather than raised. The identity is written into the detail because the operator's
    /// next question is always "which repository?", and the answer is the one thing a rejection with
    /// no run and no repository row otherwise cannot tell them.
    ///
    /// <para>Recorded at most ONCE per (hook, repository) per <see cref="UnboundAuditWindow"/>,
    /// because this being the expected case is exactly why it must not accumulate like an anomaly:
    /// bind three repositories out of a five-hundred-project group and every push in the other
    /// four-hundred-and-ninety-seven would otherwise insert a row, forever. The fact worth keeping
    /// is "deliveries are arriving for repositories you have not bound, and here is which ones" —
    /// one row a day per repository says that, and ten thousand rows say it no better while burying
    /// every other refusal in the same list.</para>
    ///
    /// <para>The suppression rides the auditor's existing idempotency key rather than a read-then-
    /// write check, so two deliveries landing together cannot both decide they are the first: the
    /// unique index settles it, and the loser is swallowed as the duplicate it is.</para>
    /// </summary>
    private async Task AuditRepositoryNotBoundAsync(IngestionSubject subject, WebhookRepositoryIdentity identity, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var named = identity.FullPath ?? identity.ExternalId;

        _logger.LogInformation("Connection webhook {WebhookId} delivered an event for unbound repository {Repository}", subject.WebhookId, named);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.RepositoryNotBound,
            Detail = $"connection webhook {subject.WebhookId} delivered an event for {named}, which is not bound in CodeSpace",
            SourceType = BuildSourceType(subject),
            ExternalEventId = TryExtractDeliveryId(headers),    // sig already passed, headers are trusted
            DedupKey = BuildUnboundDedupKey(subject.WebhookId, identity),
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One key per (hook, repository, window). The identity's own fields are used rather than the
    /// display name so two payload shapes for the same project — one carrying only the id, one
    /// carrying only the path — cannot each claim a row of their own.
    /// </summary>
    private static string BuildUnboundDedupKey(Guid connectionWebhookId, WebhookRepositoryIdentity identity)
    {
        var window = DateTimeOffset.UtcNow.Ticks / UnboundAuditWindow.Ticks;

        return $"unbound:{connectionWebhookId}:{identity.ExternalId ?? identity.FullPath}:{window}";
    }

    /// <summary>Provider-level source handle for pre-classification rejections, e.g. <c>provider.github</c>.</summary>
    private static string BuildSourceType(IngestionSubject subject) =>
        $"{WorkflowRunSourceTypes.ProviderPrefix}{subject.Provider.ToString().ToLowerInvariant()}";

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

using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
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

    /// <summary>How long one event type nothing acts on collapses into a single audit row. See <see cref="AuditEventNotMappedAsync"/>.</summary>
    private static readonly TimeSpan UnmappedAuditWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// How long one (hook, reason) refusal that anyone can cause collapses into a single row: an inactive or retired hook,
    /// a bad signature, a missing delivery id. Whoever knows a hook's URL can post as often as they like before anything is
    /// authenticated, and a row per post once pushed every genuine refusal out of the operator's newest-fifty list. One row
    /// a day still says "this is happening", as the unbound and unmapped rows already do.
    /// </summary>
    public static readonly TimeSpan RefusalAuditWindow = TimeSpan.FromDays(1);

    /// <summary>
    /// How long a hook remembers a signed body it accepted. Longer than GitHub keeps a delivery redeliverable, so a body
    /// captured from a delivery log cannot simply wait it out; a captured body older than this is still valid for as long
    /// as the secret is, which rotating the secret ends.
    /// </summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromDays(7);

    /// <summary>One key per (hook, reason, day) — see <see cref="RefusalAuditWindow"/>. The rejected: keyspace's other forms carry a delivery id or an activation, so this one cannot collide with them.</summary>
    internal static string BuildRefusalWindowKey(Guid webhookId, string reason, DateTimeOffset now) =>
        $"refused:{webhookId:N}:{reason}:{now.UtcTicks / RefusalAuditWindow.Ticks}";

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
            DedupKey = BuildRefusalWindowKey(subject.WebhookId, WorkflowRunRequestRejectionReasons.WebhookInactive, DateTimeOffset.UtcNow),
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
    ///
    /// <para>Both scopes pass through it. A per-repository hook reaches Cancelled the same two ways — a
    /// scope switch, or an unbind that could not delete it at the provider — and is the same way in
    /// the team has left.</para>
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
            DedupKey = BuildRefusalWindowKey(subject.WebhookId, WorkflowRunRequestRejectionReasons.WebhookRetired, DateTimeOffset.UtcNow),
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);

        throw new InvalidOperationException($"Webhook {subject.WebhookId} was retired ({status})");
    }

    /// <summary>
    /// A per-repository hook is about its own repository and nothing else, so a repository the team removed retires
    /// its hook with it, whatever the hook row says. The row does not always say so: an unbind deletes a Registered
    /// hook but can only CAS the others to Cancelled, and a hook the operator finished by hand never told us its
    /// remote id, so nothing could delete it at the provider. Recorded under the retired reason — to the operator
    /// it is the same fact, a way in the team has left.
    /// </summary>
    private async Task EnsureRepositoryHeldOrAuditAsync(Repository repository, IngestionSubject subject, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (repository.DeletedDate == null) return;

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.WebhookRetired,
            Detail = $"webhook {subject.WebhookId} belongs to repository {repository.Id}, which was removed, and no longer accepts deliveries",
            SourceType = BuildSourceType(subject),
            ExternalEventId = null,    // pre-classification — a retired hook's body is never read
            DedupKey = BuildRefusalWindowKey(subject.WebhookId, WorkflowRunRequestRejectionReasons.WebhookRetired, DateTimeOffset.UtcNow),
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);

        throw new InvalidOperationException($"Webhook {subject.WebhookId} belongs to removed repository {repository.Id}");
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
            DedupKey = BuildRefusalWindowKey(subject.WebhookId, WorkflowRunRequestRejectionReasons.SignatureInvalid, DateTimeOffset.UtcNow),
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
            VerificationResultJson = JsonSerializer.Serialize(new { validated = false, verifier_class = verifier.GetType().Name }),
        }, cancellationToken).ConfigureAwait(false);

        throw new UnauthorizedAccessException("Webhook signature verification failed");
    }

    /// <summary>
    /// A provider that stamps every delivery with an id (<see cref="IWebhookDeliveryIdentity"/>) sent a correctly signed
    /// one without it. A real provider never does; a tool replaying a captured body does, because without an id every
    /// post of that body would read as a new delivery. Answered 400 and recorded once per hook per day.
    /// </summary>
    private async Task EnsureDeliveryIdentifiedOrAuditAsync(IngestionSubject subject, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet<IWebhookDeliveryIdentity>(subject.Provider, out var identity) || identity == null) return;
        if (Providers.WebhookHeaderLookup.TryFind(headers, identity.DeliveryIdHeader, out var deliveryId) && !string.IsNullOrWhiteSpace(deliveryId)) return;

        _logger.LogWarning("Webhook {WebhookId} delivery was signed but carried no {Header}", subject.WebhookId, identity.DeliveryIdHeader);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.DeliveryIdMissing,
            Detail = $"a signed delivery to webhook {subject.WebhookId} carried no {identity.DeliveryIdHeader}; {subject.Provider} sends one with every delivery",
            SourceType = BuildSourceType(subject),
            ExternalEventId = null,
            DedupKey = BuildRefusalWindowKey(subject.WebhookId, WorkflowRunRequestRejectionReasons.DeliveryIdMissing, DateTimeOffset.UtcNow),
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);

        throw new Exceptions.WebhookDeliveryUnidentifiedException(subject.WebhookId, identity.DeliveryIdHeader);
    }

    /// <summary>
    /// This hook already accepted the identical signed body under another delivery id. Answered 200 like any delivery
    /// handled, and recorded once per replayed body however often it is posted — the claim key already names the body.
    /// </summary>
    private async Task AuditReplayedDeliveryAsync(IngestionSubject subject, string claimKey, string deliveryId, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        _logger.LogWarning("Webhook {WebhookId} delivery {DeliveryId} repeats a body this hook already accepted under another delivery id; not started again", subject.WebhookId, deliveryId);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.DeliveryReplayed,
            Detail = $"delivery {deliveryId} to webhook {subject.WebhookId} carries a signed body this hook already accepted under another delivery id in the last {ReplayWindow.TotalDays:0} days; nothing was started",
            SourceType = BuildSourceType(subject),
            ExternalEventId = deliveryId,
            DedupKey = $"replayed:{claimKey}",
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);
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
    /// Payload parsed fine but the normalizer returned null — a valid provider event we do not act
    /// on. Audited so an operator can answer "I sent X and nothing happened" without reading server
    /// logs.
    ///
    /// <para>Recorded at most ONCE per (hook, event type) per <see cref="UnmappedAuditWindow"/>, for
    /// the same reason the unbound case is: hooks now subscribe to everything the provider offers,
    /// so that a capability added later needs no visit to every hook to start receiving its events.
    /// The direct consequence is that MOST deliveries are events nothing acts on — a label edit, a
    /// check run, a branch protection change — and a row per delivery would add thousands a day to
    /// the same table the real refusals live in, burying them.</para>
    ///
    /// <para>One row a day per event type still answers the operator's question ("deployment events
    /// are arriving and nothing handles them"), and answers it better than ten thousand identical
    /// rows. The suppression rides the auditor's idempotency key rather than a read-then-write, so
    /// two deliveries landing together cannot both decide they are the first.</para>
    /// </summary>
    private async Task AuditEventNotMappedAsync(IngestionSubject subject, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        var eventType = ReadEventType(headers);

        _logger.LogInformation("Webhook {WebhookId} delivered {EventType}, which nothing acts on", subject.WebhookId, eventType);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = subject.RepositoryId,
            Reason = WorkflowRunRequestRejectionReasons.EventNotMapped,
            Detail = $"nothing acts on {eventType} from {subject.Provider}",
            SourceType = BuildSourceType(subject),
            ExternalEventId = TryExtractDeliveryId(headers),    // sig already passed, headers are trusted
            DedupKey = BuildUnmappedDedupKey(subject.WebhookId, eventType),
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One key per (hook, event type, window) — see <see cref="AuditEventNotMappedAsync"/>.</summary>
    private static string BuildUnmappedDedupKey(Guid webhookId, string eventType)
    {
        var window = DateTimeOffset.UtcNow.Ticks / UnmappedAuditWindow.Ticks;

        return $"unmapped:{webhookId}:{eventType}:{window}";
    }

    /// <summary>
    /// The provider's own name for what it just sent — <c>X-GitHub-Event</c> or <c>X-Gitlab-Event</c>.
    /// It is what makes one collapsed row per day USEFUL rather than merely quiet: "nothing acts on
    /// check_run" is actionable, "a payload was not mapped" is not.
    /// </summary>
    private static string ReadEventType(IReadOnlyDictionary<string, string> headers)
    {
        foreach (var name in new[] { "X-GitHub-Event", "X-Gitlab-Event" })
        {
            var match = headers.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(match.Value)) return match.Value;
        }

        return "an unnamed event";
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
    /// The delivery named a repository bound on this connection, under an owner path the hook does not cover. Recorded
    /// against that repository — the one place its operator looks — and at most once per (hook, repository) per
    /// <see cref="UnboundAuditWindow"/>: after an owner rename the old hook keeps delivering every event beside the new
    /// one, and that steady duplicate is one fact, not one row per push.
    /// </summary>
    private async Task AuditRepositoryOutsideHookOwnerAsync(IngestionSubject subject, string hookOwnerPath, BoundRepository bound, IReadOnlyDictionary<string, string> headers, CancellationToken cancellationToken)
    {
        _logger.LogInformation("Connection webhook {WebhookId} on {OwnerPath} delivered an event for repository {RepositoryId}, bound under {NamespacePath}, which it does not cover", subject.WebhookId, hookOwnerPath, bound.Id, bound.NamespacePath);

        await _auditor.WriteWebhookRejectedAsync(new WebhookRejectionContext
        {
            TeamId = subject.TeamId,
            RepositoryId = bound.Id,
            Reason = WorkflowRunRequestRejectionReasons.RepositoryOutsideHookOwner,
            Detail = $"connection webhook {subject.WebhookId} on '{hookOwnerPath}' delivered an event for {bound.FullPath}, which is bound under '{bound.NamespacePath}' — outside this hook's owner. If it moved at the provider, opening the repository re-syncs its placement.",
            SourceType = BuildSourceType(subject),
            ExternalEventId = TryExtractDeliveryId(headers),    // sig already passed, headers are trusted
            DedupKey = BuildOutsideOwnerDedupKey(subject.WebhookId, bound.Id),
            RawHeadersRedactedJson = SerializeRedactedHeaders(headers),
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>One key per (hook, bound repository, window) — keyed on the repository row, since this refusal already resolved one.</summary>
    private static string BuildOutsideOwnerDedupKey(Guid connectionWebhookId, Guid repositoryId)
    {
        var window = DateTimeOffset.UtcNow.Ticks / UnboundAuditWindow.Ticks;

        return $"outside-owner:{connectionWebhookId}:{repositoryId:N}:{window}";
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

    /// <summary>Header names, safe values, all bounded — see <see cref="RejectedDeliveryHeaders"/>.</summary>
    private static string SerializeRedactedHeaders(IReadOnlyDictionary<string, string> headers) => RejectedDeliveryHeaders.Serialize(headers);

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

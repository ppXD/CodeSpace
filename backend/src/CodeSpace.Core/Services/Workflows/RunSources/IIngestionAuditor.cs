using CodeSpace.Messages.Events;

namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// Writes <c>workflow_run_request</c> rows for ingestion failures (signature fail → bare 401;
/// normalizer returned null → silent 200; no activation matched → silent dispatcher return).
/// With these rows present, the audit view answers "why didn't my webhook fire" by surfacing
/// the rejection reason + timestamp + delivery id.
///
/// <para>All methods are idempotent on (<c>source_type</c>, <c>external_event_id</c>) per the
/// unique index — a provider retry that hits the same failure twice doesn't insert twice.
/// Implementation catches <see cref="Microsoft.EntityFrameworkCore.DbUpdateException"/> with
/// PG error code 23505 (unique violation) and returns silently.</para>
/// </summary>
public interface IIngestionAuditor
{
    /// <summary>
    /// Write a <c>Rejected</c> row capturing a webhook ingestion failure. Called from
    /// <see cref="CodeSpace.Core.Services.Webhooks.IWebhookIngestionService"/> when the
    /// signature check fails, the webhook is inactive, or the normalizer can't classify
    /// the event.
    /// </summary>
    Task WriteWebhookRejectedAsync(WebhookRejectionContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Write a <c>Rejected</c> row for a normalised event that produced zero activation
    /// matches. Called from <see cref="RunSourceDispatcher"/> after the activation lookup
    /// returns empty. The event itself was successfully verified + normalised; the
    /// rejection is "no subscriber".
    /// </summary>
    Task WriteNoMatchRejectedAsync(NormalizedEvent normalizedEvent, Guid teamId, CancellationToken cancellationToken);
}

/// <summary>Inputs for <see cref="IIngestionAuditor.WriteWebhookRejectedAsync"/>. Carries everything we know about a rejected webhook delivery.</summary>
public sealed record WebhookRejectionContext
{
    public required Guid TeamId { get; init; }

    /// <summary>String from <see cref="CodeSpace.Messages.Constants.WorkflowRunRequestRejectionReasons"/>.</summary>
    public required string Reason { get; init; }

    /// <summary>Free-text detail appended to the reason (verifier message, exception, etc.). Combined into <c>error</c> column as <c>"{Reason}: {Detail}"</c>.</summary>
    public required string Detail { get; init; }

    /// <summary>
    /// Source type for the request row. For pre-classified rejections (signature fail,
    /// webhook inactive) use the provider-level handle like <c>"provider.github"</c>;
    /// for post-classification failures (event_not_mapped) use the specific event type
    /// when known.
    /// </summary>
    public required string SourceType { get; init; }

    /// <summary>Delivery id from the provider's header (X-GitHub-Delivery, etc). Null if we couldn't extract it before failing.</summary>
    public string? ExternalEventId { get; init; }

    /// <summary>Headers with secret/auth values stripped — captured pre-failure for triage.</summary>
    public string? RawHeadersRedactedJson { get; init; }

    /// <summary>Verifier diagnostic JSON (algorithm tried, key id, etc). Populated only for signature failures.</summary>
    public string? VerificationResultJson { get; init; }
}

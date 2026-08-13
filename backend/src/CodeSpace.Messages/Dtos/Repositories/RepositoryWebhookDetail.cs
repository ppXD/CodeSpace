using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Repositories;

/// <summary>
/// One repository webhook, with the timeline of everything that went wrong registering it.
///
/// <para>The only thing said about webhooks before this existed was
/// <c>RepositoryDetail.ActiveWebhooksCount</c>, which counts Registered rows — so a repository
/// whose only hook is dead-lettered reports zero, exactly like a repository that never had one.
/// This DTO is that distinction.</para>
///
/// <para>The secret is deliberately absent. It is the one field on the row that authenticates an
/// inbound delivery, and it travels only on its own endpoint so that opening the tab does not put
/// it on the wire — see <see cref="RepositoryWebhookSecret"/>.</para>
/// </summary>
public sealed record RepositoryWebhookDetail
{
    public required Guid Id { get; init; }

    /// <summary>False when an operator disabled the hook: the provider still delivers, and ingestion rejects every delivery.</summary>
    public required bool Active { get; init; }

    public required RepositoryWebhookRegistrationStatus RegistrationStatus { get; init; }

    /// <summary>Failed attempts counted so far — the position on the backoff ladder, not a census. The census is <see cref="AttemptTimeline"/>.</summary>
    public required int Attempts { get; init; }

    public required DateTimeOffset NextAttemptAt { get; init; }

    public DateTimeOffset? LastReceivedDate { get; init; }

    public required string CallbackUrl { get; init; }

    /// <summary>Provider-assigned id. Null until the registration reaches Registered.</summary>
    public string? ExternalId { get; init; }

    public required IReadOnlyList<string> SubscribedEvents { get; init; }

    /// <summary>
    /// The newest attempt's error. Redundant with the last entry of <see cref="AttemptTimeline"/>
    /// for anything that failed after the attempt table existed — and the only account there is for
    /// anything that failed before it, which would otherwise render as "Failed, no reason given".
    /// </summary>
    public string? LastError { get; init; }

    /// <summary>Every failed attempt, oldest first.</summary>
    public required IReadOnlyList<RepositoryWebhookAttemptDetail> AttemptTimeline { get; init; }
}

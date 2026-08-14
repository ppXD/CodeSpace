namespace CodeSpace.Messages.Dtos.Providers;

/// <summary>
/// Who a webhook delivery is about, as the provider names it in its own payload. A per-repository
/// hook never needs this — its callback URL already is the answer — so this exists for the
/// connection-scoped path, where one URL carries every repository in a group.
///
/// <para>Both fields are best-effort: a provider that only puts one of them on a given event
/// shape still produces a usable identity. The match prefers <see cref="ExternalId"/> because a
/// project can be renamed or moved between groups while its id does not change.</para>
/// </summary>
public sealed record WebhookRepositoryIdentity
{
    /// <summary>Provider-assigned repository id as a string, matching <c>repository.external_id</c>. Null when the payload shape omits it.</summary>
    public string? ExternalId { get; init; }

    /// <summary>Full path including namespace ("acme/platform/api"), matching <c>repository.full_path</c>. Null when the payload shape omits it.</summary>
    public string? FullPath { get; init; }
}

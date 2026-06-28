namespace CodeSpace.Messages.Dtos.Sessions;

/// <summary>
/// One page of the sessions index, keyset-paginated for an infinite-scroll sidebar. <see cref="NextCursor"/> is an
/// opaque token the client echoes back as <c>?cursor=</c> to fetch the next (older) page — <c>null</c> on the last
/// page — and stays stable under concurrent activity, riding the <c>(team_id, last_activity_at DESC, id DESC)</c>
/// index with no deep-offset cost.
/// </summary>
public sealed record SessionPage
{
    public required IReadOnlyList<SessionSummary> Items { get; init; }

    /// <summary>Opaque cursor for the next keyset page, or <c>null</c> on the last page.</summary>
    public string? NextCursor { get; init; }
}

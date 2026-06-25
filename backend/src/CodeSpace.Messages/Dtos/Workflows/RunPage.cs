namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// One page of the runs index, in either of two modes. KEYSET (the live cockpit feed): <see cref="NextCursor"/> is an
/// opaque token the client echoes back as <c>?cursor=</c> to fetch the next (older) page — <c>null</c> on the last
/// page — and stays stable under concurrent inserts, riding the <c>(team_id, created_date DESC, id DESC)</c> index
/// with no deep-offset cost. OFFSET (numbered pages, e.g. the History list): <see cref="TotalCount"/> is the total
/// rows matching the filter, so the client can render "page X of Y" and jump to any page. Exactly one of the two is
/// populated per response — the keyset path sets <see cref="NextCursor"/> (count null); the offset path sets
/// <see cref="TotalCount"/> (cursor null).
/// </summary>
public sealed record RunPage
{
    public required IReadOnlyList<WorkflowRunSummary> Items { get; init; }

    /// <summary>Opaque cursor for the next keyset page, or <c>null</c> on the last page / for an offset page.</summary>
    public string? NextCursor { get; init; }

    /// <summary>Total rows matching the filter, for a numbered (offset) page; <c>null</c> for a keyset page (no count is run).</summary>
    public int? TotalCount { get; init; }
}

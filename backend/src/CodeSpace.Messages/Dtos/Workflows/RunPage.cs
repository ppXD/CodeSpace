namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// One keyset-paginated page of the runs index. <see cref="NextCursor"/> is an opaque token the client echoes back
/// as <c>?cursor=</c> to fetch the next (older) page; it is <c>null</c> when this is the last page. Keyset (not
/// OFFSET) so pages stay stable under concurrent inserts and the query rides the <c>(team_id, created_date DESC,
/// id DESC)</c> index with no deep-offset scan cost.
/// </summary>
public sealed record RunPage
{
    public required IReadOnlyList<WorkflowRunSummary> Items { get; init; }

    /// <summary>Opaque cursor for the next page, or <c>null</c> when there are no more rows.</summary>
    public string? NextCursor { get; init; }
}

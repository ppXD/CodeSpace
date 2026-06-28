namespace CodeSpace.Messages.Dtos.Agents;

/// <summary>
/// One server-side page of a pack's artifacts of a single kind — backs the paginated Library detail tab and the
/// New-agent / skill-binding pickers. <c>Total</c> is the full count for the (kind + search) query (it drives the
/// pager), not the size of <c>Items</c>. <c>Page</c> is the 0-based index actually returned (clamped server-side).
/// </summary>
public sealed record PagedArtifacts
{
    public required IReadOnlyList<PackArtifactSummary> Items { get; init; }
    public required int Total { get; init; }
    public required int Page { get; init; }
    public required int PageCount { get; init; }
}

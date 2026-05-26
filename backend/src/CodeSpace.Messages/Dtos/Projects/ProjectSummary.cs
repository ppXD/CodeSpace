namespace CodeSpace.Messages.Dtos.Projects;

/// <summary>
/// Operator-facing representation of a Project row. Returned by List + Get queries.
/// </summary>
public sealed record ProjectSummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
}

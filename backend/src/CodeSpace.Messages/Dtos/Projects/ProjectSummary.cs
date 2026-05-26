namespace CodeSpace.Messages.Dtos.Projects;

/// <summary>
/// Operator-facing row for the team's project list. Slug is the segment used in variable
/// references (<c>project.{Slug}.{VarName}</c>); the engine never reads the human-readable
/// <see cref="Name"/>. <see cref="ActiveRepositoryCount"/> drives the "this project has N
/// repos" badge in the list.
/// </summary>
public sealed record ProjectSummary
{
    public required Guid Id { get; init; }
    public required Guid TeamId { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required int ActiveRepositoryCount { get; init; }
    public required int ActiveVariableCount { get; init; }
}

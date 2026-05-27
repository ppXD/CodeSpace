namespace CodeSpace.Messages.Dtos.Projects;

/// <summary>
/// Lightweight project descriptor used wherever a list of project memberships is exposed
/// to the wire (e.g. <c>RepositorySummary.Projects</c>, <c>RepositoryDetail.Projects</c>).
///
/// <para>Phase 3.1 — Repository:Project is N:M via the <c>project_repository</c> link
/// table. A repo can be linked to many projects, so DTOs surface the list rather than a
/// single primary. Frontend chooses how to render (breadcrumb, chip list, etc.).</para>
/// </summary>
public sealed record ProjectRef
{
    public required Guid Id { get; init; }
    public required string Slug { get; init; }
    public required string Name { get; init; }
}

using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

public sealed record ListRepositoriesQuery : IQuery<IReadOnlyList<RepositorySummary>>, IRequireTeamMembership
{
    public Guid? ProviderInstanceId { get; init; }

    /// <summary>
    /// Phase 3.0 — filter to repositories attached to this Project. Null returns every
    /// active repo across all projects (current "Repositories" view); set to a specific id
    /// for the project-detail repo list.
    /// </summary>
    public Guid? ProjectId { get; init; }
}

using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// The teammates who can be the AUTHOR of an attributable git write on this repository — those with a
/// live linked identity on the repo's provider instance. Populates the actAsUserId picker so it only
/// offers users whose act-as write will succeed (rather than throw at write time).
/// </summary>
public sealed record ListActAsCandidatesQuery : IQuery<IReadOnlyList<ActAsCandidateSummary>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}

using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Live language composition for the Code tab's Languages bar (descending by percent). Empty when the
/// provider reports none. Membership enforced via <see cref="IRequireRepositoryAccess"/>.
/// </summary>
public sealed record GetRepositoryLanguagesQuery : IQuery<IReadOnlyList<RemoteLanguage>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}

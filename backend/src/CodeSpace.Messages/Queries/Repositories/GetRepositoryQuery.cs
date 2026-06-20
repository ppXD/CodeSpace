using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

public sealed record GetRepositoryQuery : IQuery<RepositoryDetail?>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }

    /// <summary>
    /// When true, re-sync the repo's metadata (visibility/description/default branch/…) from the provider
    /// before returning — a ~1-2s round-trip. Defaults to false so the detail page paints instantly from the
    /// stored snapshot; the page issues a second refresh=true read in the background (stale-while-revalidate).
    /// </summary>
    public bool Refresh { get; init; }
}

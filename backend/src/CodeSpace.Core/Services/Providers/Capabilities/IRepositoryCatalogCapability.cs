using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Repository discovery and lookup. Required for both binding (resolve by path) and
/// the "list available repos for this credential" UI flow.
/// </summary>
public interface IRepositoryCatalogCapability : IProviderCapability
{
    Task<RemoteRepository> GetByExternalIdAsync(ProviderContext context, string externalId, CancellationToken cancellationToken);
    Task<RemoteRepository> ResolveByPathAsync(ProviderContext context, string namespacePath, string name, CancellationToken cancellationToken);

    /// <summary>
    /// One page of repositories the credential can see, optionally filtered by a free-text
    /// search on the repo name. <paramref name="page"/> is 1-based; the provider may cap
    /// <paramref name="perPage"/> server-side (GitHub 100, GitLab 100). Callers infer
    /// "more available" from <c>result.Count == perPage</c>.
    ///
    /// Provider notes:
    ///   - <b>GitHub</b>: always hits <c>GET /user/repos</c> regardless of <paramref name="search"/>
    ///     — GitHub has no API that combines full visibility (own + collaborator + org-member)
    ///     with name search (Search API lacks an affiliation qualifier; GraphQL's
    ///     <c>viewer.repositories</c> connection lacks a search argument). The SPA eager-fetches
    ///     all pages and filters client-side.
    ///   - <b>GitLab</b>: server-side <c>?search=</c> + <c>?per_page=</c> filter both work
    ///     uniformly via <c>Projects.Get(ProjectQuery)</c> against the membership-scoped list.
    /// </summary>
    Task<RemoteRepositoryPage> PagedListAccessibleAsync(ProviderContext context, string? search, int page, int perPage, CancellationToken cancellationToken);
}

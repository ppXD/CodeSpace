using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.ReleaseCatalog;

/// <summary>
/// Live release + tag reads for the in-app Releases page. Resolves the repo's provider + credential and
/// enforces the source-read scope, same preflight as <c>IRepositoryInsightsService</c>, then invokes the
/// provider's <c>IReleaseCatalogCapability</c>. Consumers (Mediator handlers) don't see the wiring.
///
/// <para>Folder is <c>ReleaseCatalog/</c> (not <c>Releases/</c>) so it doesn't collide with the .NET
/// <c>.gitignore</c>'s <c>[Rr]eleases/</c> build-output pattern, which would silently un-track the source.</para>
/// </summary>
public interface IReleaseCatalogService
{
    Task<IReadOnlyList<RemoteRelease>> ListReleasesAsync(Guid repositoryId, int page, int perPage, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteTag>> ListTagsAsync(Guid repositoryId, int page, int perPage, CancellationToken cancellationToken);
}

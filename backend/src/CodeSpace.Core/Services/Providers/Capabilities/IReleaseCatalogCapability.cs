using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Listing releases + tags for a repository — the in-app Releases page (Releases / Tags tabs). Live read
/// against the provider, nothing cached locally. Sibling to <see cref="IPullRequestCatalogCapability"/> /
/// <see cref="IIssueCatalogCapability"/> per Rule 7: a provider without releases simply doesn't implement
/// this. (The single latest-release card stays on <c>IRepositoryInsightsCapability</c> — a Code-tab headline —
/// while the full catalog lives here.)
/// </summary>
public interface IReleaseCatalogCapability : IProviderCapability
{
    /// <summary>
    /// List releases newest-first with notes + assets, one page at a time. <paramref name="page"/> is 1-based.
    /// Exactly one returned release carries <see cref="RemoteRelease.IsLatest"/> = true (GitHub's "Latest" badge).
    /// Callers infer "there's more" from <c>result.Count == perPage</c>.
    /// </summary>
    Task<IReadOnlyList<RemoteRelease>> ListReleasesAsync(ProviderContext context, RemoteRepository repository, int page, int perPage, CancellationToken cancellationToken);

    /// <summary>Git tags newest-first, one page at a time — the Tags tab's version list. Lightweight tags have a null message.</summary>
    Task<IReadOnlyList<RemoteTag>> ListTagsAsync(ProviderContext context, RemoteRepository repository, int page, int perPage, CancellationToken cancellationToken);

    /// <summary>
    /// Fetch a single release by its tag name with full notes + assets — the in-app release-detail page.
    /// Sets <see cref="RemoteRelease.IsLatest"/> by comparing against the repo's latest release. Throws when
    /// the tag has no release (mapped to 404 by the resilience/error layer).
    /// </summary>
    Task<RemoteRelease> GetReleaseAsync(ProviderContext context, RemoteRepository repository, string tag, CancellationToken cancellationToken);
}

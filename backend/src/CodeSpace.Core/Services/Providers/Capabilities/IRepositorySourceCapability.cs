using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Browsing a repository's source at a branch/ref — the data behind the "Code" tab: the branch list,
/// one level of the file tree, and a single file's content. Live reads against the provider, never
/// cached locally (same policy as <see cref="IPullRequestCatalogCapability"/>).
///
/// Scope: repo-read — <c>repo</c>/<c>public_repo</c> on GitHub, <c>api</c>/<c>read_api</c> on GitLab —
/// the same family the repository/PR catalog already requires, so wiring this capability adds no new
/// OAuth consent for existing credentials.
/// </summary>
public interface IRepositorySourceCapability : IProviderCapability
{
    /// <summary>All branches, with the repo's default flagged via <see cref="RemoteBranch.IsDefault"/>. Provider-natural order — the SPA sorts/filters.</summary>
    Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken);

    /// <summary>
    /// One level of the tree at <paramref name="path"/> (null/empty ⇒ repo root) on
    /// <paramref name="reference"/> (null/empty ⇒ the repo's default branch). NON-recursive — the
    /// browser lazy-loads each folder as the user drills in, so a huge repo never pulls its whole
    /// tree in one call.
    /// </summary>
    Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(ProviderContext context, RemoteRepository repository, string? path, string? reference, CancellationToken cancellationToken);

    /// <summary>
    /// A single file's content at <paramref name="path"/> on <paramref name="reference"/> (null/empty ⇒
    /// default branch). Binary or oversized files come back with the corresponding flag set and
    /// <see cref="RemoteFileContent.Text"/> null — the provider never inlines non-text or megabyte payloads.
    /// </summary>
    Task<RemoteFileContent> GetFileAsync(ProviderContext context, RemoteRepository repository, string path, string? reference, CancellationToken cancellationToken);
}

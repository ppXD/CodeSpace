using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Commit history for the Code tab: the single latest commit on a path/ref (the header bar) and the
/// per-entry last commit for a folder's children (the file list's last-commit column). Live reads, same
/// repo-read scope family as source browsing.
///
/// <see cref="ListPathCommitsAsync"/> is best-effort and bounded: it costs one provider call per path, so
/// implementations cap concurrency and a path whose lookup fails is simply absent from the returned map —
/// the file list still renders, just without that row's commit.
/// </summary>
public interface IRepositoryHistoryCapability : IProviderCapability
{
    /// <summary>Latest commit affecting <paramref name="path"/> (null/empty ⇒ repo root) on <paramref name="reference"/> (null/empty ⇒ default branch). Null when the path has no history.</summary>
    Task<RemoteCommitSummary?> GetLatestCommitAsync(ProviderContext context, RemoteRepository repository, string? path, string? reference, CancellationToken cancellationToken);

    /// <summary>Latest commit for each of <paramref name="paths"/> on <paramref name="reference"/>. Best-effort: failed paths are omitted from the map.</summary>
    Task<IReadOnlyDictionary<string, RemoteCommitSummary>> ListPathCommitsAsync(ProviderContext context, RemoteRepository repository, IReadOnlyList<string> paths, string? reference, CancellationToken cancellationToken);
}

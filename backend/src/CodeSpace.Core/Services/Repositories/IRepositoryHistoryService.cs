using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Repositories;

/// <summary>
/// Live commit history for the Code tab — the latest-commit header bar and per-entry last-commit columns.
/// Same preflight (provider + credential + source-read scope) as the source/insights services.
/// </summary>
public interface IRepositoryHistoryService
{
    Task<RemoteCommitSummary?> GetLatestCommitAsync(Guid repositoryId, string? path, string? reference, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, RemoteCommitSummary>> GetTreeCommitsAsync(Guid repositoryId, IReadOnlyList<string> paths, string? reference, CancellationToken cancellationToken);
}

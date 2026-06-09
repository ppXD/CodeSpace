using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Repositories;

/// <summary>
/// Live source-browsing for a bound repository — the backend behind the Code tab. Each call resolves
/// the repo's provider + credential, enforces the source-read scope, and delegates to the provider's
/// <c>IRepositorySourceCapability</c>. Nothing is cached locally (same policy as the PR service).
/// </summary>
public interface IRepositorySourceService
{
    Task<IReadOnlyList<RemoteBranch>> ListBranchesAsync(Guid repositoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteTreeEntry>> ListTreeAsync(Guid repositoryId, string? path, string? reference, CancellationToken cancellationToken);
    Task<RemoteFileContent> GetFileAsync(Guid repositoryId, string path, string? reference, CancellationToken cancellationToken);
}

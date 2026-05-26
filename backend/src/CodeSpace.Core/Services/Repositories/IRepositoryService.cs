using CodeSpace.Messages.Dtos.Repositories;

namespace CodeSpace.Core.Services.Repositories;

/// <summary>
/// Local repository CRUD + relink. Doesn't include the provider-side fetches
/// (those live on IPullRequestService et al.) — this surface is just the
/// CodeSpace-owned records.
/// </summary>
public interface IRepositoryService
{
    Task<IReadOnlyList<RepositorySummary>> ListAsync(Guid? providerInstanceId, CancellationToken cancellationToken);
    Task<RepositoryDetail?> GetAsync(Guid repositoryId, CancellationToken cancellationToken);
    Task RelinkCredentialAsync(Guid repositoryId, Guid newCredentialId, CancellationToken cancellationToken);
}

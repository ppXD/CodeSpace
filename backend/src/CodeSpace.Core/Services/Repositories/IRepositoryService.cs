using CodeSpace.Messages.Dtos.Repositories;

namespace CodeSpace.Core.Services.Repositories;

/// <summary>
/// Local repository CRUD + relink. Doesn't include the provider-side fetches
/// (those live on IPullRequestService et al.) — this surface is just the
/// CodeSpace-owned records.
/// </summary>
public interface IRepositoryService
{
    /// <summary>
    /// Lists active bound repositories for the current team. Both filters are optional and
    /// independent — pass both null to get every repo, pass providerInstanceId to scope to
    /// one provider, pass projectId (Phase 3.0) to scope to one project's repo collection.
    /// </summary>
    Task<IReadOnlyList<RepositorySummary>> ListAsync(Guid? providerInstanceId, Guid? projectId, CancellationToken cancellationToken);

    Task<RepositoryDetail?> GetAsync(Guid repositoryId, CancellationToken cancellationToken);
    Task RelinkCredentialAsync(Guid repositoryId, Guid newCredentialId, CancellationToken cancellationToken);
}

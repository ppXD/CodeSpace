using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Repositories;

/// <summary>
/// Live repository metadata for the Code tab's right rail — stats + language composition. Resolves the
/// repo's provider + credential and enforces the source-read scope, same preflight as the source service.
/// </summary>
public interface IRepositoryInsightsService
{
    Task<RemoteRepositoryStats> GetStatsAsync(Guid repositoryId, CancellationToken cancellationToken);
    Task<IReadOnlyList<RemoteLanguage>> GetLanguagesAsync(Guid repositoryId, CancellationToken cancellationToken);
}

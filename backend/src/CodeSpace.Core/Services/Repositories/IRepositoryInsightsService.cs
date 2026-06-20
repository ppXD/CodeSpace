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

    /// <summary>The repository's latest release for the right-rail Releases card. Null when there are none.</summary>
    Task<RemoteRelease?> GetLatestReleaseAsync(Guid repositoryId, CancellationToken cancellationToken);
}

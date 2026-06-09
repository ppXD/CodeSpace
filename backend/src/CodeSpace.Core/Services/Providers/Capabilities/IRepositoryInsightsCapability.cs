using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers.Capabilities;

/// <summary>
/// Repository metadata for the Code tab's right rail: headline stats (stars / forks / counts / storage)
/// and language composition. Live reads, never cached locally. Same repo-read scope family as the source
/// capability, so it adds no new OAuth consent.
///
/// Both methods are resilient by contract: a provider that can't supply a particular number returns it as
/// null on the stats record (best-effort), and an empty language list is a valid answer — neither throws
/// just because one count or the languages endpoint is unavailable.
/// </summary>
public interface IRepositoryInsightsCapability : IProviderCapability
{
    /// <summary>Headline stats. Missing numbers come back null (the UI omits those rows).</summary>
    Task<RemoteRepositoryStats> GetStatsAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken);

    /// <summary>Language composition (descending by percent). Empty when the provider reports none.</summary>
    Task<IReadOnlyList<RemoteLanguage>> GetLanguagesAsync(ProviderContext context, RemoteRepository repository, CancellationToken cancellationToken);
}

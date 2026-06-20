using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Providers;

namespace CodeSpace.Core.Services.Providers;

/// <summary>
/// Maps a persisted <see cref="Repository"/> row to the provider-facing <see cref="RemoteRepository"/> the
/// capability calls take. One definition shared by every service that drives a provider capability from a
/// loaded repo (source / insights / history / markdown render / pull requests) — they all resolve the same
/// repo into the same shape, so the projection lives here once rather than copied into each service.
/// </summary>
public static class RepositoryProviderMapping
{
    public static RemoteRepository ToRemoteRepository(this Repository repo) => new()
    {
        ExternalId = repo.ExternalId,
        NamespacePath = repo.NamespacePath,
        Name = repo.Name,
        FullPath = repo.FullPath,
        DefaultBranch = repo.DefaultBranch,
        Visibility = repo.Visibility,
        Description = repo.Description,
        WebUrl = repo.WebUrl,
        CloneUrlHttps = repo.CloneUrlHttps,
        CloneUrlSsh = repo.CloneUrlSsh,
        Archived = repo.Archived
    };

    /// <summary>
    /// Copy live provider state onto a persisted repo row — name / path / visibility / default branch /
    /// description / clone URLs / archived. Identity-bearing fields (Id, ExternalId, CreatedDate) are left
    /// untouched. Shared by bind/resurrect and the read-through metadata refresh so the two can't drift.
    /// </summary>
    public static void ApplyRemoteMetadata(this Repository repo, RemoteRepository remote)
    {
        repo.NamespacePath = remote.NamespacePath;
        repo.Name = remote.Name;
        repo.FullPath = remote.FullPath;
        repo.DefaultBranch = remote.DefaultBranch;
        repo.Visibility = remote.Visibility;
        repo.Description = remote.Description;
        repo.WebUrl = remote.WebUrl;
        repo.CloneUrlHttps = remote.CloneUrlHttps;
        repo.CloneUrlSsh = remote.CloneUrlSsh;
        repo.Archived = remote.Archived;
    }
}

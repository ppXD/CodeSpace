using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Repositories;

public sealed class RepositoryService : IRepositoryService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;

    public RepositoryService(CodeSpaceDbContext db, ICurrentTeam currentTeam)
    {
        _db = db;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<RepositorySummary>> ListAsync(Guid? providerInstanceId, Guid? projectId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;
        var query = _db.Repository.AsNoTracking().Where(r => r.TeamId == teamId && r.DeletedDate == null);

        if (providerInstanceId.HasValue) query = query.Where(r => r.ProviderInstanceId == providerInstanceId.Value);
        if (projectId.HasValue) query = query.Where(r => r.ProjectId == projectId.Value);

        return await query
            .OrderByDescending(r => r.CreatedDate)
            .Select(r => new RepositorySummary
            {
                Id = r.Id,
                TeamId = r.TeamId,
                ProviderInstanceId = r.ProviderInstanceId,
                CredentialId = r.CredentialId,
                FullPath = r.FullPath,
                Name = r.Name,
                DefaultBranch = r.DefaultBranch,
                Visibility = r.Visibility,
                Status = r.Status,
                LastError = r.LastError,
                WebUrl = r.WebUrl,
                LastEventDate = r.LastEventDate,
                CreatedDate = r.CreatedDate
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<RepositoryDetail?> GetAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        return await _db.Repository
            .AsNoTracking()
            .Where(r => r.Id == repositoryId && r.DeletedDate == null)
            .Select(r => new RepositoryDetail
            {
                Id = r.Id,
                TeamId = r.TeamId,
                ProviderInstanceId = r.ProviderInstanceId,
                CredentialId = r.CredentialId,
                ExternalId = r.ExternalId,
                NamespacePath = r.NamespacePath,
                Name = r.Name,
                FullPath = r.FullPath,
                DefaultBranch = r.DefaultBranch,
                Visibility = r.Visibility,
                Description = r.Description,
                WebUrl = r.WebUrl,
                CloneUrlHttps = r.CloneUrlHttps,
                CloneUrlSsh = r.CloneUrlSsh,
                Archived = r.Archived,
                LastSyncedDate = r.LastSyncedDate,
                LastEventDate = r.LastEventDate,
                Status = r.Status,
                LastError = r.LastError,
                CreatedDate = r.CreatedDate,
                // "Active" here means "alive at the provider AND we haven't unbound it" —
                // i.e. RegistrationStatus = Registered AND the Active flag is still true.
                // Pending / Enqueued / Registering / Failed rows represent in-flight registrations
                // (the webhook isn't live on the remote yet); Cancelled / DeadLettered are dead.
                // Only Registered counts toward the "this repo is hooked up" indicator.
                ActiveWebhooksCount = _db.RepositoryWebhook.Count(w => w.RepositoryId == r.Id && w.Active && w.RegistrationStatus == Messages.Enums.RepositoryWebhookRegistrationStatus.Registered)
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RelinkCredentialAsync(Guid repositoryId, Guid newCredentialId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var repo = await LoadOwnedRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);
        var credential = await LoadActiveCandidateCredentialAsync(newCredentialId, teamId, cancellationToken).ConfigureAwait(false);
        EnsureSameProviderInstance(repo, credential);

        repo.CredentialId = credential.Id;
        repo.Status = RepositoryStatus.Active;
        repo.LastError = null;
    }

    private async Task<Repository> LoadOwnedRepositoryAsync(Guid id, Guid teamId, CancellationToken cancellationToken)
    {
        var repo = await _db.Repository.SingleOrDefaultAsync(r => r.Id == id && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Repository {id} not found");

        // Defence in depth — IRequireRepositoryAccess already enforces tenancy at the
        // pipeline layer, but the explicit team_id check here protects against any future
        // marker-interface drift.
        if (repo.TeamId != teamId) throw new KeyNotFoundException($"Repository {id} not found");

        return repo;
    }

    /// <summary>
    /// Candidate credential must be Active and in the same team. We don't require it to be
    /// OAuth — a team that uses a PAT credential as fallback should be able to swap to it.
    /// </summary>
    private async Task<Credential> LoadActiveCandidateCredentialAsync(Guid id, Guid teamId, CancellationToken cancellationToken)
    {
        var credential = await _db.Credential.SingleOrDefaultAsync(c => c.Id == id && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Credential {id} not found or no longer active");

        if (credential.TeamId != teamId) throw new InvalidOperationException($"Credential {id} not found or no longer active");

        if (credential.Status != CredentialStatus.Active) throw new InvalidOperationException($"Credential '{credential.DisplayName}' is {credential.Status} — pick an Active credential.");

        return credential;
    }

    /// <summary>
    /// A repo can only borrow a credential bound to its own provider instance. Otherwise
    /// the OAuth/PAT token would talk to the wrong host and every API call would 401/404.
    /// </summary>
    private static void EnsureSameProviderInstance(Repository repo, Credential credential)
    {
        if (credential.ProviderInstanceId != repo.ProviderInstanceId)
            throw new InvalidOperationException($"Credential '{credential.DisplayName}' is for a different provider instance. Pick a credential connected to the same provider as the repository.");
    }
}

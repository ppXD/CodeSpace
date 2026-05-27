using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Dtos.Projects;
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
        // Phase 3.1 — Repository:Project is N:M via the link table. "Filter by project" is
        // "the repo has an active link to this project".
        if (projectId.HasValue) query = query.Where(r => _db.ProjectRepository.Any(pr => pr.RepositoryId == r.Id && pr.ProjectId == projectId.Value && pr.DeletedDate == null));

        var bare = await query
            .OrderByDescending(r => r.CreatedDate)
            .Select(r => new
            {
                r.Id,
                r.TeamId,
                r.ProviderInstanceId,
                r.CredentialId,
                r.FullPath,
                r.Name,
                r.DefaultBranch,
                r.Visibility,
                r.Status,
                r.LastError,
                r.WebUrl,
                r.LastEventDate,
                r.CreatedDate,
                Projects = _db.ProjectRepository
                    .Where(pr => pr.RepositoryId == r.Id && pr.DeletedDate == null)
                    .OrderBy(pr => pr.CreatedDate)
                    .Select(pr => new ProjectRef { Id = pr.Project.Id, Slug = pr.Project.Slug, Name = pr.Project.Name })
                    .ToList()
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return bare.Select(r => new RepositorySummary
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
            CreatedDate = r.CreatedDate,
            Projects = r.Projects,
        }).ToList();
    }

    public async Task<RepositoryDetail?> GetAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        // Phase 3.1 — Repository:Project is N:M. The detail DTO surfaces every active
        // project link; the legacy ProjectId/Slug/Name fields are derived from the first
        // link (by ascending CreatedDate) so existing SPA breadcrumbs keep working until
        // they migrate to the Projects[] field. Returns null Project* when the repo has
        // no active project links (e.g. operator hasn't attached it after the 0026 schema
        // change wiped legacy links).
        var bare = await _db.Repository
            .AsNoTracking()
            .Where(r => r.Id == repositoryId && r.DeletedDate == null)
            .Select(r => new
            {
                Repo = r,
                Projects = _db.ProjectRepository
                    .Where(pr => pr.RepositoryId == r.Id && pr.DeletedDate == null)
                    .OrderBy(pr => pr.CreatedDate)
                    .Select(pr => new ProjectRef { Id = pr.Project.Id, Slug = pr.Project.Slug, Name = pr.Project.Name })
                    .ToList(),
                // "Active" here means "alive at the provider AND we haven't unbound it" —
                // i.e. RegistrationStatus = Registered AND the Active flag is still true.
                // Pending / Enqueued / Registering / Failed rows represent in-flight registrations
                // (the webhook isn't live on the remote yet); Cancelled / DeadLettered are dead.
                // Only Registered counts toward the "this repo is hooked up" indicator.
                ActiveWebhooksCount = _db.RepositoryWebhook.Count(w => w.RepositoryId == r.Id && w.Active && w.RegistrationStatus == Messages.Enums.RepositoryWebhookRegistrationStatus.Registered)
            })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (bare == null) return null;

        var primary = bare.Projects.FirstOrDefault();
        return new RepositoryDetail
        {
            Id = bare.Repo.Id,
            TeamId = bare.Repo.TeamId,
            ProviderInstanceId = bare.Repo.ProviderInstanceId,
            CredentialId = bare.Repo.CredentialId,
            Projects = bare.Projects,
            ProjectId = primary?.Id,
            ProjectSlug = primary?.Slug,
            ProjectName = primary?.Name,
            ExternalId = bare.Repo.ExternalId,
            NamespacePath = bare.Repo.NamespacePath,
            Name = bare.Repo.Name,
            FullPath = bare.Repo.FullPath,
            DefaultBranch = bare.Repo.DefaultBranch,
            Visibility = bare.Repo.Visibility,
            Description = bare.Repo.Description,
            WebUrl = bare.Repo.WebUrl,
            CloneUrlHttps = bare.Repo.CloneUrlHttps,
            CloneUrlSsh = bare.Repo.CloneUrlSsh,
            Archived = bare.Repo.Archived,
            LastSyncedDate = bare.Repo.LastSyncedDate,
            LastEventDate = bare.Repo.LastEventDate,
            Status = bare.Repo.Status,
            LastError = bare.Repo.LastError,
            CreatedDate = bare.Repo.CreatedDate,
            ActiveWebhooksCount = bare.ActiveWebhooksCount,
        };
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

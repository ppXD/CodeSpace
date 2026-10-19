using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Core.Services.RepositoryBinding;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Messages.Dtos.Projects;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Repositories;

public sealed class RepositoryService : IRepositoryService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;
    private readonly IProviderRegistry _registry;
    private readonly IConnectionWebhookCoverageService _hookCoverage;
    private readonly IRepositoryRehoming _rehoming;
    private readonly IRepositoryReadProbe _probe;
    private readonly ILogger<RepositoryService> _logger;

    public RepositoryService(CodeSpaceDbContext db, ICurrentTeam currentTeam, ICurrentUser currentUser, IProviderRegistry registry, IConnectionWebhookCoverageService hookCoverage, IRepositoryRehoming rehoming, IRepositoryReadProbe probe, ILogger<RepositoryService> logger)
    {
        _db = db;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
        _registry = registry;
        _hookCoverage = hookCoverage;
        _rehoming = rehoming;
        _probe = probe;
        _logger = logger;
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

    public async Task<RepositoryDetail?> GetAsync(Guid repositoryId, bool refresh, CancellationToken cancellationToken)
    {
        // Stale-while-revalidate: the stored metadata (visibility, default branch, description, archived, …)
        // is a snapshot from bind time and drifts when the repo changes on the provider (e.g. a repo flipped
        // private→public). The default read serves that snapshot instantly so the detail page never blocks on
        // the network; the page then issues a second refresh=true read in the background, and ONLY that path
        // pays the provider round-trip to re-sync + persist the live values.
        if (refresh)
            await RefreshMetadataBestEffortAsync(repositoryId, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Best-effort live re-sync of a repo's mirror-of-remote metadata. Loads the current repo from the
    /// provider and applies it via the shared <see cref="RepositoryProviderMapping.ApplyRemoteMetadata"/>,
    /// stamping <c>LastSyncedDate</c>. Any failure (unbound / missing credential / insufficient scope /
    /// rate limit / network) is swallowed — the detail still renders from the last-known stored values
    /// rather than erroring over a freshness refresh. Uses the repo's OWN connection credential, so it
    /// needs no per-caller scope (membership is already enforced by the query's IRequireRepositoryAccess).
    ///
    /// <para>This is where CodeSpace learns a repository moved owners at the provider, and on a connection-scoped
    /// instance the move takes it out from under the hook that covered its old path. So a hook for the new path is
    /// staged in the SAME commit as the new path — a refresh that persisted the move without it would leave every
    /// later delivery refused, and no later refresh would notice, because the path would no longer change.</para>
    /// </summary>
    private async Task RefreshMetadataBestEffortAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        try
        {
            var repo = await _db.Repository
                .Include(r => r.ProviderInstance)
                .Include(r => r.Credential)
                .SingleOrDefaultAsync(r => r.Id == repositoryId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false);

            if (repo?.Credential == null) return;   // unbound / no credential → nothing live to read

            var catalog = _registry.Require<IRepositoryCatalogCapability>(repo.ProviderInstance.Provider);
            var context = new ProviderContext(repo.ProviderInstance, repo.Credential);
            var remote = await catalog.GetByExternalIdAsync(context, repo.ExternalId, cancellationToken).ConfigureAwait(false);
            var previousNamespacePath = repo.NamespacePath;

            repo.ApplyRemoteMetadata(remote);
            repo.LastSyncedDate = DateTimeOffset.UtcNow;

            var stagedHookId = await _hookCoverage.StageForMoveAsync(repo, previousNamespacePath, cancellationToken).ConfigureAwait(false);

            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await _hookCoverage.DispatchAfterCommitAsync(stagedHookId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Best-effort metadata refresh failed for repository {RepositoryId}; serving stored values", repositoryId);
        }
    }

    public async Task RelinkCredentialAsync(Guid repositoryId, Guid newCredentialId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id!.Value;

        var repo = await LoadOwnedRepositoryAsync(repositoryId, teamId, cancellationToken).ConfigureAwait(false);
        var credential = await LoadActiveCandidateCredentialAsync(newCredentialId, teamId, cancellationToken).ConfigureAwait(false);

        if (credential.ProviderInstanceId != repo.ProviderInstanceId) await MoveToAnotherConnectionAsync(repo, credential, teamId, cancellationToken).ConfigureAwait(false);
        else repo.CredentialId = credential.Id;

        repo.Status = RepositoryStatus.Active;
        repo.LastError = null;
    }

    /// <summary>
    /// A credential of another connection to the same host takes the repository to its connection. Refused for a different host with the message a
    /// credential of another host always got; and, since this reaches further than the repository's own connection, held to what the other doors hold a
    /// move to — the caller's own credential or a team token (the repository would act as that person), a connection that does not already hold the
    /// project, a credential whose scopes cover the hook it will register — and to reading the repository where CodeSpace has it, before anything is written.
    /// </summary>
    private async Task MoveToAnotherConnectionAsync(Repository repo, Credential credential, Guid teamId, CancellationToken cancellationToken)
    {
        await EnsureSameHostAsync(repo, credential, teamId, cancellationToken).ConfigureAwait(false);
        EnsureCallerMayBindWith(credential);
        await EnsureNotRefusedAsync(repo, credential, cancellationToken).ConfigureAwait(false);
        await EnsureReadAsync(repo, credential, cancellationToken).ConfigureAwait(false);

        var staged = await _rehoming.RehomeAsync(repo, credential, cancellationToken).ConfigureAwait(false);

        await _rehoming.DispatchAfterCommitAsync(staged, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureSameHostAsync(Repository repo, Credential credential, Guid teamId, CancellationToken cancellationToken)
    {
        if (await ProviderInstanceHost.AreInterchangeableAsync(_db, teamId, repo.ProviderInstanceId, credential.ProviderInstanceId, cancellationToken).ConfigureAwait(false)) return;

        throw new InvalidOperationException($"Credential '{credential.DisplayName}' is for a different provider instance. Pick a credential connected to the same provider as the repository.");
    }

    private void EnsureCallerMayBindWith(Credential credential)
    {
        if (_currentUser.Id is Guid callerId && RelinkSuggestionRanking.IsCallersOrTeams(credential, callerId)) return;

        throw new InvalidOperationException($"Credential '{credential.DisplayName}' is another person's personal connection, and a repository bound through it would act as them. Pick one of your own connections, or a team token.");
    }

    private async Task EnsureNotRefusedAsync(Repository repo, Credential credential, CancellationToken cancellationToken)
    {
        var refused = await _rehoming.RefusalsAsync(new[] { repo }, credential, cancellationToken).ConfigureAwait(false);

        if (refused.TryGetValue(repo.Id, out var reason)) throw new InvalidOperationException(reason);
    }

    /// <summary>Ask the provider, as the credential, for the repository by its id — and expect it at the path CodeSpace has it, which tells it from another repository (or the same one, renamed since CodeSpace last read it).</summary>
    private async Task EnsureReadAsync(Repository repo, Credential credential, CancellationToken cancellationToken)
    {
        var read = await _probe.ReadAsync(credential, repo.ExternalId, sourceName: null, cancellationToken).ConfigureAwait(false);

        if (!read.Readable)
            throw new InvalidOperationException($"Credential '{credential.DisplayName}' cannot read '{repo.FullPath}'. Nothing was moved. Pick a credential that reaches it, or unbind the repository first.");

        if (!read.IsAt(repo.FullPath))
            throw new InvalidOperationException($"Credential '{credential.DisplayName}' does not find '{repo.FullPath}' where CodeSpace has it — the provider names that repository '{read.FullPath}'. Nothing was moved. Pick another credential, or unbind the repository and bind it again.");
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
}

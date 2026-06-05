using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Projects;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Core.Settings.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.RepositoryBinding;

public sealed class RepositoryBindingService : IRepositoryBindingService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IProviderRegistry _registry;
    private readonly IProviderEventSubscriptionRegistry _subscriptionRegistry;
    private readonly IPayloadEncryptor _encryptor;
    private readonly IScopeChecker _scopeChecker;
    private readonly IRepositoryWebhookRegistrationDispatcher _registrationDispatcher;
    private readonly IProjectService _projectService;
    private readonly WebhookBaseUrlSetting _webhookBaseUrl;
    private readonly ILogger<RepositoryBindingService> _logger;

    public RepositoryBindingService(CodeSpaceDbContext db, IProviderRegistry registry, IProviderEventSubscriptionRegistry subscriptionRegistry, IPayloadEncryptor encryptor, IScopeChecker scopeChecker, IRepositoryWebhookRegistrationDispatcher registrationDispatcher, IProjectService projectService, WebhookBaseUrlSetting webhookBaseUrl, ILogger<RepositoryBindingService> logger)
    {
        _db = db;
        _registry = registry;
        _subscriptionRegistry = subscriptionRegistry;
        _encryptor = encryptor;
        _scopeChecker = scopeChecker;
        _registrationDispatcher = registrationDispatcher;
        _projectService = projectService;
        _webhookBaseUrl = webhookBaseUrl;
        _logger = logger;
    }

    public async Task<BulkBindResult> BindManyAsync(BindRepositoriesBulkRequest request, CancellationToken cancellationToken)
    {
        // All-or-nothing per the interface contract — the TransactionalBehavior pipeline
        // step around the calling handler owns the rollback on exception. We iterate
        // BindAsync in declared order so the rollback set is bounded to "everything bound
        // up to the failing identifier"; a parallel implementation would explode the
        // rollback surface for no real win since IO-bound bind calls already overlap
        // inside the underlying provider clients.
        var items = new List<BulkBindItemResult>(request.ProjectIdentifiers.Count);

        foreach (var identifier in request.ProjectIdentifiers)
        {
            var bind = new BindRepositoryRequest
            {
                TeamId = request.TeamId,
                ProviderInstanceId = request.ProviderInstanceId,
                CredentialId = request.CredentialId,
                ProjectIdentifier = identifier,
                ProjectId = request.ProjectId,
            };

            var repo = await BindAsync(bind, cancellationToken).ConfigureAwait(false);
            items.Add(new BulkBindItemResult { ProjectIdentifier = identifier, RepositoryId = repo.Id });
        }

        return new BulkBindResult { Items = items, SuccessCount = items.Count, FailureCount = 0 };
    }

    public async Task<Repository> BindAsync(BindRepositoryRequest request, CancellationToken cancellationToken)
    {
        var ctx = new BindContext { Request = request };

        ctx = await LoadProviderInstanceAsync(ctx, cancellationToken).ConfigureAwait(false);
        ctx = await LoadCredentialAsync(ctx, cancellationToken).ConfigureAwait(false);
        EnsureCredentialMatchesInstance(ctx);
        ctx = ResolveProvider(ctx);
        EnsureCredentialCoversBindCapabilities(ctx);   // pre-flight scope check — fail fast before any wire call
        await EnsureCredentialIsValidAsync(ctx, cancellationToken).ConfigureAwait(false);
        ctx = await ResolveRemoteRepositoryAsync(ctx, cancellationToken).ConfigureAwait(false);
        ctx = await ResolveOrResurrectRepositoryAsync(ctx, cancellationToken).ConfigureAwait(false);
        ctx = await ResolveProjectAsync(ctx, cancellationToken).ConfigureAwait(false);
        ctx = GenerateWebhookIdAndSecret(ctx);

        PersistRepositoryAndPendingWebhook(ctx, out var repository);

        // Flush the Repository + Pending webhook row to the DB so the dispatcher's CAS
        // UPDATE can see them. Same shape as Workflows.WorkflowService.RunManuallyAsync:
        // a process crash AFTER SaveChanges but BEFORE DispatchAsync leaves the row in
        // Pending, and the stuck-webhook reconciler picks it up within ~2 minutes. No
        // double-execution: dispatcher's Pending→Enqueued CAS + registrar's Enqueued→
        // Registering CAS + provider idempotency-by-callback-URL together ensure at most
        // one remote hook lands.
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        // No webhook was created when re-using an already-active repo (only a project link was added).
        if (!ctx.ReusingActiveRepository)
            await _registrationDispatcher.DispatchAsync(ctx.NewWebhookId, cancellationToken).ConfigureAwait(false);

        return repository;
    }

    /// <summary>
    /// Two capabilities matter during bind:
    ///   • <see cref="IRepositoryCatalogCapability"/> — to resolve/list the remote repo
    ///   • <see cref="IWebhookRegistrationCapability"/> — to register the webhook async
    /// Both must be backed by the credential's granted scopes. Reject with the typed
    /// exception so the frontend can render a precise "go to GitLab/GitHub and add scope X"
    /// message instead of letting the user proceed and fail mid-flight on webhook register.
    /// </summary>
    private void EnsureCredentialCoversBindCapabilities(BindContext ctx)
    {
        _scopeChecker.EnsureCapability(ctx.Credential, ctx.Instance.Provider, typeof(IRepositoryCatalogCapability));
        _scopeChecker.EnsureCapability(ctx.Credential, ctx.Instance.Provider, typeof(IWebhookRegistrationCapability));
    }

    /// <summary>
    /// Unbind a repository. N:M-aware:
    ///   • <paramref name="projectId"/> given → remove only THAT project's link. If the repo is still
    ///     in another project, keep the repo + its webhook (only the link goes). If it was the last
    ///     project, fall through to full teardown.
    ///   • <paramref name="projectId"/> null → "remove entirely": soft-delete every project link, then
    ///     tear down webhooks + the repo row (team-level removal).
    /// </summary>
    public async Task<Unit> UnbindAsync(Guid repositoryId, Guid? projectId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);

        if (projectId.HasValue)
        {
            var link = await _db.ProjectRepository
                .SingleOrDefaultAsync(pr => pr.RepositoryId == repositoryId && pr.ProjectId == projectId.Value && pr.DeletedDate == null, cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Repository {repo.FullPath} is not in project {projectId.Value}.");

            link.DeletedDate = DateTimeOffset.UtcNow;

            // Still linked to another project → the repo (and its single webhook) stays; only this link goes.
            var otherActiveLinks = await _db.ProjectRepository
                .CountAsync(pr => pr.RepositoryId == repositoryId && pr.ProjectId != projectId.Value && pr.DeletedDate == null, cancellationToken)
                .ConfigureAwait(false);

            if (otherActiveLinks > 0) return Unit.Value;
        }
        else
        {
            await SoftDeleteAllProjectLinksAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        }

        // No active project link remains (or an unscoped removal) → tear the repo + its webhooks down.
        await TearDownRepositoryAsync(repo, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }

    private async Task SoftDeleteAllProjectLinksAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var links = await _db.ProjectRepository
            .Where(pr => pr.RepositoryId == repositoryId && pr.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        var now = DateTimeOffset.UtcNow;
        foreach (var link in links) link.DeletedDate = now;
    }

    /// <summary>
    /// Delete the repo's remote webhooks (best-effort) and soft-delete the row. For Registered rows,
    /// hard-delete the local record (the remote hook is gone, no audit value). Non-terminal rows
    /// (Pending / Enqueued / Registering / Failed) CAS to Cancelled so any in-flight registrar /
    /// dispatcher tick sees the terminal state and no-ops. DeadLettered rows stay for operator triage.
    /// </summary>
    private async Task TearDownRepositoryAsync(Repository repo, CancellationToken cancellationToken)
    {
        var webhooks = await LoadAllWebhooksAsync(repo.Id, cancellationToken).ConfigureAwait(false);
        var registeredWebhooks = webhooks.Where(w => w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registered).ToList();

        await BestEffortDeleteRemoteWebhooksAsync(repo, registeredWebhooks, cancellationToken).ConfigureAwait(false);
        await CancelNonTerminalWebhooksAsync(repo.Id, cancellationToken).ConfigureAwait(false);

        _db.RepositoryWebhook.RemoveRange(registeredWebhooks);
        repo.DeletedDate = DateTimeOffset.UtcNow;
    }

    public async Task<CredentialProbeResult> TestAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);

        if (repo.Credential == null) return new CredentialProbeResult { IsValid = false, Error = "Repository has no credential bound" };

        var prober = _registry.Require<ICredentialProbeCapability>(repo.ProviderInstance.Provider);
        var providerContext = new ProviderContext(repo.ProviderInstance, repo.Credential);

        return await prober.ProbeCredentialAsync(providerContext, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BindContext> LoadProviderInstanceAsync(BindContext ctx, CancellationToken cancellationToken)
    {
        var instance = await _db.ProviderInstance.SingleOrDefaultAsync(p => p.Id == ctx.Request.ProviderInstanceId && p.TeamId == ctx.Request.TeamId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"ProviderInstance {ctx.Request.ProviderInstanceId} not found in team {ctx.Request.TeamId}");

        return ctx with { Instance = instance };
    }

    private async Task<BindContext> LoadCredentialAsync(BindContext ctx, CancellationToken cancellationToken)
    {
        var credential = await _db.Credential.SingleOrDefaultAsync(c => c.Id == ctx.Request.CredentialId && c.TeamId == ctx.Request.TeamId, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Credential {ctx.Request.CredentialId} not found in team {ctx.Request.TeamId}");

        return ctx with { Credential = credential };
    }

    /// <summary>
    /// The chosen credential must belong to the SAME provider instance we're binding against. Both are
    /// already team-scoped (LoadProviderInstance / LoadCredential), but within one team a caller can still
    /// pair instance A1 with a credential of instance A2 — the token would then talk to the wrong host and
    /// every API call would 401/404 mid-bind. Mirrors RepositoryService's relink-side
    /// EnsureSameProviderInstance so bind and relink enforce the identical invariant.
    /// </summary>
    private static void EnsureCredentialMatchesInstance(BindContext ctx)
    {
        if (ctx.Credential.ProviderInstanceId != ctx.Instance.Id)
            throw new InvalidOperationException($"Credential '{ctx.Credential.DisplayName}' is for a different provider instance. Pick a credential connected to the same provider instance you're binding.");
    }

    private BindContext ResolveProvider(BindContext ctx)
    {
        return ctx with
        {
            Catalog = _registry.Require<IRepositoryCatalogCapability>(ctx.Instance.Provider),
            Prober = _registry.Require<ICredentialProbeCapability>(ctx.Instance.Provider)
        };
    }

    private async Task EnsureCredentialIsValidAsync(BindContext ctx, CancellationToken cancellationToken)
    {
        var providerContext = new ProviderContext(ctx.Instance, ctx.Credential);
        var probe = await ctx.Prober.ProbeCredentialAsync(providerContext, cancellationToken).ConfigureAwait(false);

        if (!probe.IsValid) throw new InvalidOperationException($"Credential probe failed: {probe.Error ?? "unknown error"}");
    }

    private async Task<BindContext> ResolveRemoteRepositoryAsync(BindContext ctx, CancellationToken cancellationToken)
    {
        var providerContext = new ProviderContext(ctx.Instance, ctx.Credential);
        var identifier = ctx.Request.ProjectIdentifier.Trim();

        var remote = identifier.Contains('/')
            ? await ResolveByPathAsync(ctx.Catalog, providerContext, identifier, cancellationToken).ConfigureAwait(false)
            : await ctx.Catalog.GetByExternalIdAsync(providerContext, identifier, cancellationToken).ConfigureAwait(false);

        return ctx with { Remote = remote };
    }

    private static async Task<RemoteRepository> ResolveByPathAsync(IRepositoryCatalogCapability catalog, ProviderContext providerContext, string fullPath, CancellationToken cancellationToken)
    {
        var lastSlash = fullPath.LastIndexOf('/');
        var namespacePath = fullPath.Substring(0, lastSlash);
        var name = fullPath.Substring(lastSlash + 1);

        return await catalog.ResolveByPathAsync(providerContext, namespacePath, name, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// TeamCity-style identity continuity. A Repository row is durable across:
    ///   • Unbind → re-add (same provider_instance) — soft-deleted row resurrects
    ///   • Provider instance recreated — old row was under instance-A, new bind targets
    ///     instance-B for the same (provider_kind, base_url) → row resurrects + re-points
    /// The identity key is broader than the partial unique index: we look up by
    /// <c>(team_id, provider_kind, base_url, external_id)</c> across ALL rows (active +
    /// soft-deleted) so future PR / AI-review / chat rows that FK to Repository.Id
    /// survive a disconnect / re-OAuth / rebind cycle without becoming orphans.
    ///
    /// Three outcomes:
    ///   • Active row found at the same identity → RE-USE it (N:M): the repo is already imported,
    ///     so this bind just attaches it to another project. No metadata mutation, no new webhook —
    ///     the persist step adds the project link (or rejects if it's already in the target project).
    ///   • Soft-deleted row found → resurrect: clear DeletedDate, re-point to current
    ///     instance + credential, refresh metadata from the remote (path / name / visibility
    ///     may have changed while unbound), flip back to Active. Gets a fresh webhook.
    ///   • Nothing found → leave Repository null on the context; the persistence step will
    ///     INSERT a fresh row.
    /// </summary>
    private async Task<BindContext> ResolveOrResurrectRepositoryAsync(BindContext ctx, CancellationToken cancellationToken)
    {
        var candidate = await _db.Repository
            .Include(r => r.ProviderInstance)
            .Where(r => r.TeamId == ctx.Instance.TeamId
                     && r.ProviderInstance.Provider == ctx.Instance.Provider
                     && r.ProviderInstance.BaseUrl == ctx.Instance.BaseUrl
                     && r.ExternalId == ctx.Remote.ExternalId)
            // Prefer an active row (re-use for N:M). Otherwise the most-recently-deleted
            // candidate (in case there are multiple historical bindings) to resurrect.
            .OrderBy(r => r.DeletedDate == null ? 0 : 1)
            .ThenByDescending(r => r.DeletedDate ?? r.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (candidate == null) return ctx with { ExistingRepository = null };

        // Already active → re-use as-is (the persist step adds the project link). N:M: the same repo
        // can belong to many projects, so a second bind is not an error — it's another link.
        if (candidate.DeletedDate == null) return ctx with { ExistingRepository = candidate, ReusingActiveRepository = true };

        candidate.DeletedDate = null;
        candidate.ProviderInstanceId = ctx.Instance.Id;
        candidate.CredentialId = ctx.Credential.Id;
        candidate.Status = RepositoryStatus.Active;
        candidate.LastError = null;
        ApplyRemoteMetadata(candidate, ctx.Remote);

        return ctx with { ExistingRepository = candidate };
    }

    /// <summary>
    /// Refresh fields that mirror remote state. Called on resurrect because the repo's
    /// path / name / visibility / default branch may have changed while it was unbound on
    /// our side. Identity-bearing fields (Id, ExternalId, CreatedDate) are deliberately
    /// untouched — that's the whole point of resurrecting instead of re-creating.
    /// </summary>
    private static void ApplyRemoteMetadata(Repository repo, RemoteRepository remote)
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

    private static BindContext GenerateWebhookIdAndSecret(BindContext ctx)
    {
        return ctx with
        {
            NewWebhookId = Guid.NewGuid(),
            WebhookSecret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        };
    }

    /// <summary>
    /// Persist the Repository + a Pending RepositoryWebhook + the Project-Repository link
    /// in the same SaveChanges (one EF transaction). The webhook row carries the durable
    /// intent — secret encrypted, callback URL committed — so if the process crashes
    /// BEFORE we get to call the dispatcher, the reconciler picks the row up within ~2
    /// minutes and re-dispatches.
    ///
    /// <para>Phase 3.1 — project membership lives in the <c>project_repository</c> link
    /// table. New binds always create a link row to <see cref="BindContext.EffectiveProjectId"/>.
    /// Resurrected repositories: only create a link if one isn't already active for the
    /// target project (idempotent re-bind preserves existing N:M membership).</para>
    /// </summary>
    private void PersistRepositoryAndPendingWebhook(BindContext ctx, out Repository repository)
    {
        // Re-using an ALREADY-ACTIVE repo that's already in the target project is a true duplicate
        // (same repo, same project). The picker disables these, so this is a safety net. (Resurrect
        // never hits this — its links were soft-deleted on unbind; AttachProjectLink revives them.)
        if (ctx.ReusingActiveRepository && ctx.ExistingTargetLink is { DeletedDate: null })
            throw new InvalidOperationException($"Repository {ctx.Remote.FullPath} is already in this project.");

        repository = ctx.ExistingRepository ?? BuildRepositoryEntity(ctx);

        if (ctx.ExistingRepository == null) _db.Repository.Add(repository);

        AttachProjectLink(ctx, repository.Id);

        // A re-used active repo already has a registered webhook delivering its events — registering
        // a second would duplicate deliveries. Only new + resurrected rows need a fresh webhook.
        if (!ctx.ReusingActiveRepository)
            _db.RepositoryWebhook.Add(BuildPendingWebhookEntity(ctx, repository.Id));
    }

    /// <summary>
    /// Link the repository to the target project: INSERT a fresh row, or — because the composite
    /// (ProjectId, RepositoryId) PK forbids a duplicate — revive a soft-deleted row from a prior
    /// membership. An already-active link is left untouched (the resurrect path re-binding to a
    /// project it never left).
    /// </summary>
    private void AttachProjectLink(BindContext ctx, Guid repositoryId)
    {
        if (ctx.ExistingTargetLink == null)
        {
            var now = DateTimeOffset.UtcNow;
            _db.ProjectRepository.Add(new ProjectRepository
            {
                ProjectId = ctx.EffectiveProjectId,
                RepositoryId = repositoryId,
                TeamId = ctx.Request.TeamId,
                CreatedDate = now,
                LastModifiedDate = now,
            });
            return;
        }

        if (ctx.ExistingTargetLink.DeletedDate != null)
        {
            ctx.ExistingTargetLink.DeletedDate = null;
            ctx.ExistingTargetLink.LastModifiedDate = DateTimeOffset.UtcNow;
        }
    }

    private static Repository BuildRepositoryEntity(BindContext ctx) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = ctx.Request.TeamId,
        ProviderInstanceId = ctx.Instance.Id,
        CredentialId = ctx.Credential.Id,
        ExternalId = ctx.Remote.ExternalId,
        NamespacePath = ctx.Remote.NamespacePath,
        Name = ctx.Remote.Name,
        FullPath = ctx.Remote.FullPath,
        DefaultBranch = ctx.Remote.DefaultBranch,
        Visibility = ctx.Remote.Visibility,
        Description = ctx.Remote.Description,
        WebUrl = ctx.Remote.WebUrl,
        CloneUrlHttps = ctx.Remote.CloneUrlHttps,
        CloneUrlSsh = ctx.Remote.CloneUrlSsh,
        Archived = ctx.Remote.Archived,
        Status = RepositoryStatus.Active
    };

    /// <summary>
    /// Resolve the target Project + pre-load existing active links so the persist step
    /// can skip duplicate insertions. Caller-supplied
    /// <see cref="BindRepositoryRequest.ProjectId"/> wins (we verify it belongs to the
    /// same team to prevent cross-team binding); otherwise fall back to the team's
    /// "default" project (lazily created if missing).
    ///
    /// <para>Phase 3.1 — Repository:Project is N:M. Resurrected repositories may already
    /// have project links from their prior active life; we pre-load them so the persist
    /// step won't double-insert (composite PK would fail). Fresh binds have no existing
    /// links by definition.</para>
    /// </summary>
    private async Task<BindContext> ResolveProjectAsync(BindContext ctx, CancellationToken cancellationToken)
    {
        Guid effectiveProjectId;
        if (ctx.Request.ProjectId.HasValue)
        {
            var ok = await _db.Project.AsNoTracking()
                .AnyAsync(p => p.Id == ctx.Request.ProjectId.Value && p.TeamId == ctx.Request.TeamId && p.DeletedDate == null, cancellationToken)
                .ConfigureAwait(false);
            if (!ok)
                throw new InvalidOperationException(
                    $"Project {ctx.Request.ProjectId.Value} not found in team {ctx.Request.TeamId}. Choose a project that belongs to this team.");
            effectiveProjectId = ctx.Request.ProjectId.Value;
        }
        else
        {
            effectiveProjectId = await _projectService.EnsureDefaultProjectAsync(ctx.Request.TeamId, cancellationToken).ConfigureAwait(false);
        }

        // Load the (target project, repo) link — TRACKED, so persist can resurrect a soft-deleted
        // one instead of INSERTing a duplicate. Only an existing repo can carry a link; a fresh
        // bind's new id has none. Composite PK means there's at most one row (active or soft-deleted).
        ProjectRepository? targetLink = null;
        if (ctx.ExistingRepository != null)
        {
            targetLink = await _db.ProjectRepository
                .SingleOrDefaultAsync(pr => pr.RepositoryId == ctx.ExistingRepository.Id && pr.ProjectId == effectiveProjectId, cancellationToken)
                .ConfigureAwait(false);
        }

        return ctx with { EffectiveProjectId = effectiveProjectId, ExistingTargetLink = targetLink };
    }

    /// <summary>
    /// Construct the Pending webhook row. <c>ExternalId</c> stays null until the registrar
    /// reaches Registered; the secret is encrypted before persistence so an attacker with
    /// DB read access cannot recover it without the master key.
    /// </summary>
    private RepositoryWebhook BuildPendingWebhookEntity(BindContext ctx, Guid repositoryId)
    {
        var callbackUrl = $"{_webhookBaseUrl.Value.TrimEnd('/')}/api/webhooks/{ctx.NewWebhookId}";
        var subscribedEvents = _subscriptionRegistry.GetSubscribedRawEvents(ctx.Instance.Provider);

        return new RepositoryWebhook
        {
            Id = ctx.NewWebhookId,
            RepositoryId = repositoryId,
            ExternalId = null,
            CallbackUrl = callbackUrl,
            SecretEnc = _encryptor.Encrypt(ctx.WebhookSecret),
            SubscribedEvents = subscribedEvents.ToList(),
            Active = true,
            RegistrationStatus = RepositoryWebhookRegistrationStatus.Pending,
            NextAttemptAt = DateTimeOffset.UtcNow
        };
    }

    private async Task<Repository> LoadRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        return await _db.Repository
            .Include(r => r.ProviderInstance)
            .Include(r => r.Credential)
            .SingleOrDefaultAsync(r => r.Id == repositoryId && r.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found or already deleted");
    }

    private async Task<List<RepositoryWebhook>> LoadAllWebhooksAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        return await _db.RepositoryWebhook.Where(w => w.RepositoryId == repositoryId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically flip every non-terminal webhook on this repository to Cancelled. The CAS
    /// WHERE clause is the unbind-vs-in-flight-registration race protector: a registrar
    /// that's currently Registering this row will lose the race (its Registering→Registered
    /// CAS sees Cancelled, no-ops). A dispatcher about to Enqueue this row will lose the
    /// race (its Pending→Enqueued CAS sees Cancelled, no-ops). End state: at most one remote
    /// hook landed (and BestEffortDeleteRemoteWebhooksAsync removed it), no orphans.
    /// </summary>
    private async Task CancelNonTerminalWebhooksAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var nonTerminal = new[]
        {
            RepositoryWebhookRegistrationStatus.Pending,
            RepositoryWebhookRegistrationStatus.Enqueued,
            RepositoryWebhookRegistrationStatus.Registering,
            RepositoryWebhookRegistrationStatus.Failed
        };

        await _db.RepositoryWebhook
            .Where(w => w.RepositoryId == repositoryId && nonTerminal.Contains(w.RegistrationStatus))
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Cancelled), cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task BestEffortDeleteRemoteWebhooksAsync(Repository repo, List<RepositoryWebhook> webhooks, CancellationToken cancellationToken)
    {
        if (repo.Credential == null || webhooks.Count == 0) return;

        var catalog = _registry.Require<IRepositoryCatalogCapability>(repo.ProviderInstance.Provider);
        var webhookCap = _registry.Require<IWebhookRegistrationCapability>(repo.ProviderInstance.Provider);
        var providerContext = new ProviderContext(repo.ProviderInstance, repo.Credential);

        RemoteRepository? remote = null;
        try
        {
            remote = await catalog.GetByExternalIdAsync(providerContext, repo.ExternalId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not load remote repo {RepoId} during unbind; will still remove local records", repo.Id);
            return;
        }

        foreach (var webhook in webhooks)
        {
            // Defensive: a Registered row must have an external_id by construction (the
            // registrar wrote it atomically with the status transition), but we guard
            // anyway so a future schema change can't crash unbind.
            if (string.IsNullOrEmpty(webhook.ExternalId)) continue;

            try
            {
                await webhookCap.DeleteWebhookAsync(providerContext, remote, webhook.ExternalId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete remote webhook {WebhookId} on provider; will still remove local record", webhook.Id);
            }
        }
    }

    private sealed record BindContext
    {
        public required BindRepositoryRequest Request { get; init; }

        public ProviderInstance Instance { get; init; } = default!;
        public Credential Credential { get; init; } = default!;
        public IRepositoryCatalogCapability Catalog { get; init; } = default!;
        public ICredentialProbeCapability Prober { get; init; } = default!;
        public RemoteRepository Remote { get; init; } = default!;

        /// <summary>
        /// The existing Repository row this bind re-uses — either an ALREADY-ACTIVE row (the repo is
        /// imported already; we just attach it to another project, N:M) or a soft-deleted row we
        /// resurrect. Null on a first-time bind; persistence INSERTs a fresh row then. Re-using a row
        /// preserves its Id so any FK chain (PR / review / chat) survives.
        /// </summary>
        public Repository? ExistingRepository { get; init; }

        /// <summary>True when <see cref="ExistingRepository"/> was already active — the bind then only
        /// adds a project link, registering NO new webhook (the existing one already delivers events).</summary>
        public bool ReusingActiveRepository { get; init; }

        /// <summary>
        /// The project the new (or resurrected) repository row will be attached to via the
        /// <c>project_repository</c> link table. Filled by <see cref="ResolveProjectAsync"/>
        /// from either the caller's explicit ProjectId (after team-membership check) or the
        /// team's lazily-created Default project.
        /// </summary>
        public Guid EffectiveProjectId { get; init; }

        /// <summary>
        /// The existing <c>project_repository</c> row for (<see cref="EffectiveProjectId"/>, repo), if
        /// any — active OR soft-deleted (composite PK ⇒ at most one). Lets persist resurrect a
        /// soft-deleted link rather than INSERT a duplicate (PK violation), and detect the
        /// "already in this project" duplicate. Null when no row exists yet (a fresh link to insert).
        /// </summary>
        public ProjectRepository? ExistingTargetLink { get; init; }

        public Guid NewWebhookId { get; init; }
        public string WebhookSecret { get; init; } = default!;
    }
}

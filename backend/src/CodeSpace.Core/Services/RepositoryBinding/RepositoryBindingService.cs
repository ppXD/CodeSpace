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

    public async Task<Unit> UnbindAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var repo = await LoadRepositoryAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        var webhooks = await LoadAllWebhooksAsync(repositoryId, cancellationToken).ConfigureAwait(false);

        var registeredWebhooks = webhooks.Where(w => w.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registered).ToList();
        await BestEffortDeleteRemoteWebhooksAsync(repo, registeredWebhooks, cancellationToken).ConfigureAwait(false);

        // For Registered rows, hard-delete: the remote hook is gone, no audit value in
        // keeping the row. For non-terminal rows (Pending / Enqueued / Registering / Failed),
        // CAS to Cancelled so any in-flight registrar / dispatcher tick that races us sees
        // the terminal state and no-ops. DeadLettered rows stay as-is for operator triage —
        // they're already terminal.
        await CancelNonTerminalWebhooksAsync(repositoryId, cancellationToken).ConfigureAwait(false);
        _db.RepositoryWebhook.RemoveRange(registeredWebhooks);
        repo.DeletedDate = DateTimeOffset.UtcNow;

        return Unit.Value;
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
    ///   • Active row found at the same identity → throw "already bound" with the existing
    ///     row's id, so the operator knows what to unbind first.
    ///   • Soft-deleted row found → resurrect: clear DeletedDate, re-point to current
    ///     instance + credential, refresh metadata from the remote (path / name / visibility
    ///     may have changed while unbound), flip back to Active. EF tracks the row already.
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
            // Prefer an active row if one exists (we'll reject it). Otherwise the most-
            // recently-deleted candidate (in case there are multiple historical bindings).
            .OrderBy(r => r.DeletedDate == null ? 0 : 1)
            .ThenByDescending(r => r.DeletedDate ?? r.CreatedDate)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (candidate?.DeletedDate == null && candidate != null)
        {
            throw new InvalidOperationException($"Repository {ctx.Remote.FullPath} is already bound (id={candidate.Id}). Unbind it first if you want to re-bind.");
        }

        if (candidate == null) return ctx with { ResurrectedRepository = null };

        candidate.DeletedDate = null;
        candidate.ProviderInstanceId = ctx.Instance.Id;
        candidate.CredentialId = ctx.Credential.Id;
        candidate.Status = RepositoryStatus.Active;
        candidate.LastError = null;
        ApplyRemoteMetadata(candidate, ctx.Remote);

        return ctx with { ResurrectedRepository = candidate };
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
        repository = ctx.ResurrectedRepository ?? BuildRepositoryEntity(ctx);
        var pendingWebhook = BuildPendingWebhookEntity(ctx, repository.Id);

        if (ctx.ResurrectedRepository == null) _db.Repository.Add(repository);
        _db.RepositoryWebhook.Add(pendingWebhook);

        // Attach the project link if it doesn't already exist as active. We need the
        // pre-loaded list from ctx.ExistingActiveProjectLinks (populated by
        // ResolveProjectAsync) so EF doesn't double-insert and trip the composite-PK
        // unique violation.
        if (!ctx.ExistingActiveProjectLinkProjectIds.Contains(ctx.EffectiveProjectId))
        {
            var now = DateTimeOffset.UtcNow;
            _db.ProjectRepository.Add(new ProjectRepository
            {
                ProjectId = ctx.EffectiveProjectId,
                RepositoryId = repository.Id,
                TeamId = ctx.Request.TeamId,
                CreatedDate = now,
                LastModifiedDate = now,
            });
        }
    }

    private static Repository BuildRepositoryEntity(BindContext ctx) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = ctx.Request.TeamId,
        // Legacy column dual-written during the Phase 3.1 transition; the project_repository
        // link row (created in PersistRepositoryAndPendingWebhook) is the new source of truth.
        ProjectId = ctx.EffectiveProjectId,
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

        // Pre-load active project links for THIS repo (only relevant for resurrected
        // rows; fresh binds will have a new Guid that's not in the table yet).
        var existingProjectIds = new HashSet<Guid>();
        if (ctx.ResurrectedRepository != null)
        {
            var rows = await _db.ProjectRepository.AsNoTracking()
                .Where(pr => pr.RepositoryId == ctx.ResurrectedRepository.Id && pr.DeletedDate == null)
                .Select(pr => pr.ProjectId)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            foreach (var pid in rows) existingProjectIds.Add(pid);
        }

        return ctx with
        {
            EffectiveProjectId = effectiveProjectId,
            ExistingActiveProjectLinkProjectIds = existingProjectIds,
        };
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
        /// Set when a previously-unbound row with the same identity exists and we should
        /// re-use its Id (preserving any FK chain — future PR / review / chat tables).
        /// Null on first-time binds; persistence INSERTs a new row in that case.
        /// </summary>
        public Repository? ResurrectedRepository { get; init; }

        /// <summary>
        /// The project the new (or resurrected) repository row will be attached to via the
        /// <c>project_repository</c> link table. Filled by <see cref="ResolveProjectAsync"/>
        /// from either the caller's explicit ProjectId (after team-membership check) or the
        /// team's lazily-created Default project.
        /// </summary>
        public Guid EffectiveProjectId { get; init; }

        /// <summary>
        /// Project ids that <see cref="ResolveProjectAsync"/> observed as already-active
        /// links for this repository (only populated for resurrected repos). Persist step
        /// uses this to skip duplicate INSERTs that would trip the composite-PK uniqueness.
        /// </summary>
        public IReadOnlySet<Guid> ExistingActiveProjectLinkProjectIds { get; init; } = new HashSet<Guid>();

        public Guid NewWebhookId { get; init; }
        public string WebhookSecret { get; init; } = default!;
    }
}

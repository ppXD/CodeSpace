using System.Security.Cryptography;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Outbox;
using CodeSpace.Core.Services.Outbox.Payloads;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.Scopes;
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
    private readonly WebhookBaseUrlSetting _webhookBaseUrl;
    private readonly ILogger<RepositoryBindingService> _logger;

    public RepositoryBindingService(CodeSpaceDbContext db, IProviderRegistry registry, IProviderEventSubscriptionRegistry subscriptionRegistry, IPayloadEncryptor encryptor, IScopeChecker scopeChecker, WebhookBaseUrlSetting webhookBaseUrl, ILogger<RepositoryBindingService> logger)
    {
        _db = db;
        _registry = registry;
        _subscriptionRegistry = subscriptionRegistry;
        _encryptor = encryptor;
        _scopeChecker = scopeChecker;
        _webhookBaseUrl = webhookBaseUrl;
        _logger = logger;
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
        ctx = GenerateWebhookIdAndSecret(ctx);

        return PersistRepositoryAndEnqueueWebhookRegistration(ctx);
    }

    /// <summary>
    /// Two capabilities matter during bind:
    ///   • <see cref="IRepositoryCatalogCapability"/> — to resolve/list the remote repo
    ///   • <see cref="IWebhookRegistrationCapability"/> — to register the webhook later (outbox)
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
        var webhooks = await LoadActiveWebhooksAsync(repositoryId, cancellationToken).ConfigureAwait(false);

        await BestEffortDeleteRemoteWebhooksAsync(repo, webhooks, cancellationToken).ConfigureAwait(false);

        _db.RepositoryWebhook.RemoveRange(webhooks);
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

    private Repository PersistRepositoryAndEnqueueWebhookRegistration(BindContext ctx)
    {
        // Resurrect path: the row is already EF-tracked from the lookup, just need the
        // outbox row for the new webhook (old RepositoryWebhook rows were hard-deleted on
        // Unbind, so we always register a fresh one). Insert path: brand-new entity.
        var repository = ctx.ResurrectedRepository ?? BuildRepositoryEntity(ctx);
        var outboxMessage = BuildRegisterWebhookOutboxMessage(ctx, repository.Id);

        if (ctx.ResurrectedRepository == null) _db.Repository.Add(repository);
        _db.OutboxMessage.Add(outboxMessage);

        return repository;
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

    private OutboxMessage BuildRegisterWebhookOutboxMessage(BindContext ctx, Guid repositoryId)
    {
        var callbackUrl = $"{_webhookBaseUrl.Value.TrimEnd('/')}/api/webhooks/{ctx.NewWebhookId}";
        var subscribedEvents = _subscriptionRegistry.GetSubscribedRawEvents(ctx.Instance.Provider);
        var payload = new RegisterWebhookOutboxPayload
        {
            WebhookId = ctx.NewWebhookId,
            RepositoryId = repositoryId,
            CallbackUrl = callbackUrl,
            Secret = ctx.WebhookSecret,
            SubscribedEvents = subscribedEvents
        };

        return new OutboxMessage
        {
            Id = Guid.NewGuid(),
            AggregateType = nameof(Repository),
            AggregateId = repositoryId,
            MessageType = OutboxMessageTypes.RegisterWebhook,
            Payload = JsonSerializer.Serialize(payload),
            Status = OutboxStatus.Pending,
            NextAttemptDate = DateTimeOffset.UtcNow
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

    private async Task<List<RepositoryWebhook>> LoadActiveWebhooksAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        return await _db.RepositoryWebhook.Where(w => w.RepositoryId == repositoryId && w.Active).ToListAsync(cancellationToken).ConfigureAwait(false);
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

        public Guid NewWebhookId { get; init; }
        public string WebhookSecret { get; init; } = default!;
    }
}

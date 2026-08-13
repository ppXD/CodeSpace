using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks;

public sealed class RepositoryWebhookService : IRepositoryWebhookService, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly IRepositoryWebhookRegistrationDispatcher _dispatcher;
    private readonly IPayloadEncryptor _encryptor;
    private readonly IPostCommitActions _postCommit;
    private readonly ICurrentUser _currentUser;
    private readonly ILogger<RepositoryWebhookService> _logger;

    public RepositoryWebhookService(CodeSpaceDbContext db, IRepositoryWebhookRegistrationDispatcher dispatcher, IPayloadEncryptor encryptor, IPostCommitActions postCommit, ICurrentUser currentUser, ILogger<RepositoryWebhookService> logger)
    {
        _db = db;
        _dispatcher = dispatcher;
        _encryptor = encryptor;
        _postCommit = postCommit;
        _currentUser = currentUser;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RepositoryWebhookDetail>> ListAsync(Guid repositoryId, CancellationToken cancellationToken) =>
        await Project(_db.RepositoryWebhook.AsNoTracking().Where(w => w.RepositoryId == repositoryId).OrderBy(w => w.CreatedDate))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<RepositoryWebhookSecret> RevealSecretAsync(Guid repositoryId, Guid webhookId, CancellationToken cancellationToken)
    {
        var ciphertext = await LoadSecretCiphertextAsync(repositoryId, webhookId, cancellationToken).ConfigureAwait(false);

        var secret = _encryptor.Decrypt(ciphertext);

        RecordReveal(repositoryId, webhookId);

        return new RepositoryWebhookSecret { WebhookId = webhookId, Secret = secret };
    }

    public async Task<RepositoryWebhookDetail> RetryRegistrationAsync(Guid repositoryId, Guid webhookId, CancellationToken cancellationToken)
    {
        var observed = await LoadRegistrationStatusAsync(repositoryId, webhookId, cancellationToken).ConfigureAwait(false);

        EnsureRetryable(webhookId, observed);

        await ReviveAsync(webhookId, observed, cancellationToken).ConfigureAwait(false);

        // Deferred to after the revival commits, same as bind does: a worker that fetched the job
        // first would find the row still DeadLettered and its Enqueued → Registering CAS would
        // no-op, leaving the hook parked until the reconciler's next sweep. So the row the caller
        // gets back reads Pending, and the dispatcher takes it the moment the command commits.
        await _postCommit.RunAfterCommitAsync(ct => _dispatcher.DispatchAsync(webhookId, ct), cancellationToken).ConfigureAwait(false);

        return await RequireDetailAsync(repositoryId, webhookId, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The one projection, shared by the list and by the row a retry hands back, so the two can
    /// never disagree about what a webhook looks like. <c>secret_enc</c> is not selected at all —
    /// the ciphertext never enters this path, so no later edit can leak it by forgetting to drop a
    /// field.
    /// </summary>
    private IQueryable<RepositoryWebhookDetail> Project(IQueryable<RepositoryWebhook> webhooks) =>
        webhooks.Select(w => new RepositoryWebhookDetail
        {
            Id = w.Id,
            Active = w.Active,
            RegistrationStatus = w.RegistrationStatus,
            Attempts = w.Attempts,
            NextAttemptAt = w.NextAttemptAt,
            LastReceivedDate = w.LastReceivedDate,
            CallbackUrl = w.CallbackUrl,
            ExternalId = w.ExternalId,
            SubscribedEvents = w.SubscribedEvents,
            LastError = w.LastError,
            // Ordered by time rather than by attempt_number: a manual retry restarts the ladder, so
            // the numbers are not monotonic across one, and the clock is.
            AttemptTimeline = _db.RepositoryWebhookAttempt
                .Where(a => a.RepositoryWebhookId == w.Id)
                .OrderBy(a => a.AttemptedAt).ThenBy(a => a.AttemptNumber)
                .Select(a => new RepositoryWebhookAttemptDetail
                {
                    AttemptNumber = a.AttemptNumber,
                    AttemptedAt = a.AttemptedAt,
                    Error = a.Error,
                    StatusCode = a.StatusCode,
                    ResponseBody = a.ResponseBody,
                    RequestMethod = a.RequestMethod,
                    RequestUrl = a.RequestUrl,
                    RequestBody = a.RequestBody,
                    RequestHeadersJson = a.RequestHeadersJson,
                })
                .ToList(),
        });

    private async Task<RepositoryWebhookDetail> RequireDetailAsync(Guid repositoryId, Guid webhookId, CancellationToken cancellationToken) =>
        await Project(_db.RepositoryWebhook.AsNoTracking().Where(w => w.Id == webhookId && w.RepositoryId == repositoryId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Webhook {webhookId} not found on repository {repositoryId}");

    /// <summary>
    /// Keyed by BOTH ids on purpose. The pipeline vetted the repository against the caller's team;
    /// nothing has vetted the webhook id, and without this filter one from another team's repository
    /// would be readable through any repository the caller does hold.
    /// </summary>
    private async Task<string> LoadSecretCiphertextAsync(Guid repositoryId, Guid webhookId, CancellationToken cancellationToken) =>
        await _db.RepositoryWebhook.AsNoTracking()
            .Where(w => w.Id == webhookId && w.RepositoryId == repositoryId)
            .Select(w => w.SecretEnc)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Webhook {webhookId} not found on repository {repositoryId}");

    /// <summary>
    /// The only durable account of a reveal. Warning rather than Information because the question it
    /// answers is asked after an incident — "who could have forged those deliveries" — and an
    /// Information line is the first thing a retention policy drops.
    /// </summary>
    private void RecordReveal(Guid repositoryId, Guid webhookId) =>
        _logger.LogWarning(
            "Webhook signing secret for {WebhookId} on repository {RepositoryId} was revealed to user {UserId} ({UserName}). Anyone holding it can sign a delivery this repository will accept.",
            webhookId, repositoryId, _currentUser.Id, _currentUser.Name);

    private async Task<RepositoryWebhookRegistrationStatus> LoadRegistrationStatusAsync(Guid repositoryId, Guid webhookId, CancellationToken cancellationToken) =>
        await _db.RepositoryWebhook.AsNoTracking()
            .Where(w => w.Id == webhookId && w.RepositoryId == repositoryId)
            .Select(w => (RepositoryWebhookRegistrationStatus?)w.RegistrationStatus)
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Webhook {webhookId} not found on repository {repositoryId}");

    private static void EnsureRetryable(Guid webhookId, RepositoryWebhookRegistrationStatus observed)
    {
        if (observed is RepositoryWebhookRegistrationStatus.Failed or RepositoryWebhookRegistrationStatus.DeadLettered) return;

        throw new InvalidOperationException($"Webhook {webhookId} is {observed} — only a Failed or DeadLettered registration can be re-queued.");
    }

    /// <summary>
    /// The reconciler's revival, on demand: CAS to Pending and let the dispatcher take it. The WHERE
    /// is guarded on the state we read, so a reconciler tick that got there first wins and the
    /// dispatcher's own <c>Pending → Enqueued</c> CAS turns our follow-up call into a no-op.
    ///
    /// <para>Attempts resets because the ladder is the point. A DeadLettered row already sits at
    /// MaxAttempts, so reviving it without the reset buys exactly one try — the next transient
    /// timeout re-buries it, and the operator who just fixed the credential is back where they
    /// started. The count on the row is a position on that ladder, not a census; the census is the
    /// attempt timeline, which is append-only and unaffected.</para>
    /// </summary>
    private async Task ReviveAsync(Guid webhookId, RepositoryWebhookRegistrationStatus observed, CancellationToken cancellationToken) =>
        await _db.RepositoryWebhook
            .Where(w => w.Id == webhookId && w.RegistrationStatus == observed)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Pending)
                .SetProperty(w => w.Attempts, 0)
                .SetProperty(w => w.NextAttemptAt, DateTimeOffset.UtcNow), cancellationToken)
            .ConfigureAwait(false);
}

using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Webhooks;

public sealed class ConnectionWebhookCoverageReader : IConnectionWebhookCoverageReader, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;

    public ConnectionWebhookCoverageReader(CodeSpaceDbContext db) { _db = db; }

    public async Task<RepositoryWebhookCoverage> GetForRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken)
    {
        var placement = await LoadPlacementAsync(repositoryId, cancellationToken).ConfigureAwait(false);

        if (placement.Scope == ProviderWebhookScope.Repository) return new RepositoryWebhookCoverage { Scope = placement.Scope };

        var hook = await LoadCoveringHookAsync(placement, cancellationToken).ConfigureAwait(false);

        if (hook == null) return new RepositoryWebhookCoverage { Scope = placement.Scope };

        return new RepositoryWebhookCoverage { Scope = placement.Scope, OwnerPath = hook.OwnerPath, Hook = await ProjectAsync(hook, cancellationToken).ConfigureAwait(false) };
    }

    private async Task<RepositoryPlacement> LoadPlacementAsync(Guid repositoryId, CancellationToken cancellationToken) =>
        await _db.Repository.AsNoTracking()
            .Where(r => r.Id == repositoryId)
            .Select(r => new RepositoryPlacement { ProviderInstanceId = r.ProviderInstanceId, NamespacePath = r.NamespacePath, Scope = r.ProviderInstance.WebhookScope })
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"Repository {repositoryId} not found");

    /// <summary>
    /// The nearest hook above this repository, by the same ancestor rule the provisioner registers
    /// under, and on the same in-service rule — anything else would show one hook on the page and
    /// register another. Cancelled is the only exclusion: it is evidence of what was tried, not a
    /// claim that anything is covered, and it is the one state ingestion also refuses.
    /// </summary>
    private async Task<ConnectionWebhook?> LoadCoveringHookAsync(RepositoryPlacement placement, CancellationToken cancellationToken)
    {
        var candidatePaths = OwnerPathHierarchy.SelfAndAncestors(placement.NamespacePath);

        var live = await _db.ConnectionWebhook.AsNoTracking()
            .Where(w => w.ProviderInstanceId == placement.ProviderInstanceId && candidatePaths.Contains(w.OwnerPath) && WebhookRegistrationLifecycle.InService.Contains(w.RegistrationStatus))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return candidatePaths.Select(path => live.FirstOrDefault(w => w.OwnerPath == path)).FirstOrDefault(hook => hook != null);
    }

    /// <summary>
    /// Into the repository hook's own shape, field for field, so the page reads it with the reader it
    /// already has. <c>secret_enc</c> is not selected at all — this hook's secret belongs to the
    /// connection and is not one repository's to hand out.
    /// </summary>
    private async Task<RepositoryWebhookDetail> ProjectAsync(ConnectionWebhook hook, CancellationToken cancellationToken) => new()
    {
        Id = hook.Id,
        Active = hook.Active,
        RegistrationStatus = hook.RegistrationStatus,
        Attempts = hook.Attempts,
        NextAttemptAt = hook.NextAttemptAt,
        LastReceivedDate = hook.LastReceivedDate,
        CallbackUrl = hook.CallbackUrl,
        ExternalId = hook.ExternalId,
        SubscribedEvents = hook.SubscribedEvents,
        LastError = hook.LastError,
        AttemptTimeline = await LoadAttemptsAsync(hook.Id, cancellationToken).ConfigureAwait(false)
    };

    /// <summary>Ordered by time rather than attempt number, as the repository timeline is — a retry restarts the ladder, so the numbers are not monotonic across one and the clock is.</summary>
    private async Task<IReadOnlyList<RepositoryWebhookAttemptDetail>> LoadAttemptsAsync(Guid connectionWebhookId, CancellationToken cancellationToken) =>
        await _db.ConnectionWebhookAttempt.AsNoTracking()
            .Where(a => a.ConnectionWebhookId == connectionWebhookId)
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
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private sealed record RepositoryPlacement
    {
        public required Guid ProviderInstanceId { get; init; }
        public required string NamespacePath { get; init; }
        public required ProviderWebhookScope Scope { get; init; }
    }
}

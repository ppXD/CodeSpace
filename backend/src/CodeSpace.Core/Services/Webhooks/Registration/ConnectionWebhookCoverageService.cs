using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Middlewares.Transactional;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// See <see cref="IConnectionWebhookCoverageService"/>. Adds coverage and never removes it: after a rename the old hook
/// is still the one delivering for every repository whose path has not been re-synced yet, and a rename and a transfer
/// look identical from the paths alone — so retiring or re-keying it here would drop live traffic on a guess.
/// </summary>
public sealed class ConnectionWebhookCoverageService : IConnectionWebhookCoverageService, IScopedDependency
{
    private readonly IConnectionWebhookProvisioner _provisioner;
    private readonly IConnectionWebhookRegistrationDispatcher _dispatcher;
    private readonly IPostCommitActions _postCommit;
    private readonly ILogger<ConnectionWebhookCoverageService> _logger;

    public ConnectionWebhookCoverageService(IConnectionWebhookProvisioner provisioner, IConnectionWebhookRegistrationDispatcher dispatcher, IPostCommitActions postCommit, ILogger<ConnectionWebhookCoverageService> logger)
    {
        _provisioner = provisioner;
        _dispatcher = dispatcher;
        _postCommit = postCommit;
        _logger = logger;
    }

    public async Task<Guid?> StageForMoveAsync(Repository repository, string previousNamespacePath, CancellationToken cancellationToken)
    {
        if (!MovedOnConnectionScope(repository, previousNamespacePath)) return null;

        _logger.LogInformation("Repository {RepositoryId} moved at the provider from {PreviousNamespacePath} to {NamespacePath}; ensuring a connection hook covers its new owner", repository.Id, previousNamespacePath, repository.NamespacePath);

        return await _provisioner.EnsureForOwnerAsync(repository.ProviderInstance, repository.CredentialId!.Value, repository.NamespacePath, cancellationToken).ConfigureAwait(false);
    }

    public async Task DispatchAfterCommitAsync(Guid? connectionWebhookId, CancellationToken cancellationToken)
    {
        if (connectionWebhookId == null) return;

        await _postCommit.RunAfterCommitAsync(ct => _dispatcher.DispatchAsync(connectionWebhookId.Value, ct), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A per-repository hook follows its repository whatever the path says, and a repository with no credential has no identity to register a hook with.</summary>
    private static bool MovedOnConnectionScope(Repository repository, string previousNamespacePath) =>
        repository.ProviderInstance.WebhookScope == ProviderWebhookScope.Connection
        && repository.CredentialId != null
        && !string.Equals(repository.NamespacePath, previousNamespacePath, StringComparison.Ordinal);
}

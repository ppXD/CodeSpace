using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.RepositoryBinding;

/// <summary>
/// A repository that moves to another connection to the same host (<see cref="IRepositoryRehoming"/>). It lives with the bind because it is the
/// same business done to an existing row: what a bind stages for a new repository — a per-repository hook, or the owner hook a
/// connection-wide scope needs — a move stages for the connection the repository arrives at, and what an unbind retires — the
/// repository's hooks — a move retires under the connection it leaves.
/// </summary>
public sealed partial class RepositoryBindingService
{
    public async Task<IReadOnlyDictionary<Guid, string>> RefusalsAsync(IReadOnlyList<Repository> repositories, Credential to, CancellationToken cancellationToken)
    {
        var refusals = new Dictionary<Guid, string>();

        if (repositories.Count == 0) return refusals;

        var instance = await InstanceOfAsync(to, cancellationToken).ConfigureAwait(false);
        var taken = await ExternalIdsHeldAsync(repositories, instance, cancellationToken).ConfigureAwait(false);

        foreach (var repository in repositories)
        {
            var refusal = !taken.Add(repository.ExternalId) ? AlreadyBound(repository, instance) : ScopeRefusal(repository, to, instance);

            if (refusal is not null) refusals[repository.Id] = refusal;
        }

        return refusals;
    }

    public async Task<RehomeStaged> RehomeAsync(Repository repository, Credential to, CancellationToken cancellationToken)
    {
        var instance = await InstanceOfAsync(to, cancellationToken).ConfigureAwait(false);
        var retired = await RetireHooksAsync(repository, instance, to, cancellationToken).ConfigureAwait(false);

        repository.ProviderInstanceId = instance.Id;
        repository.CredentialId = to.Id;

        var staged = await StageCoverageAsync(repository, instance, to, cancellationToken).ConfigureAwait(false);

        return staged with { Retired = retired };
    }

    public async Task DispatchAfterCommitAsync(RehomeStaged staged, CancellationToken cancellationToken)
    {
        if (staged.RepositoryHookId is Guid hookId)
            await _postCommit.RunAfterCommitAsync(ct => _registrationDispatcher.DispatchAsync(hookId, ct), cancellationToken).ConfigureAwait(false);

        if (staged.ConnectionHookId is Guid groupHookId)
            await _postCommit.RunAfterCommitAsync(ct => _connectionDispatcher.DispatchAsync(groupHookId, ct), cancellationToken).ConfigureAwait(false);

        if (staged.Retired is { } retired)
            await _postCommit.RunAfterCommitAsync(ct => DeleteRetiredAtProviderAsync(retired, ct), cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProviderInstance> InstanceOfAsync(Credential to, CancellationToken cancellationToken) =>
        to.ProviderInstance is { } loaded && loaded.Id == to.ProviderInstanceId
            ? loaded
            : await _db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == to.ProviderInstanceId, cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// The external ids, among these repositories', that the new connection already holds a live row for: <c>idx_repository_provider_instance_external_id_active</c>
    /// would refuse the move at the save, with nothing a person could read in it. A set the caller keeps adding to, so two repositories of one batch that name the
    /// same project meet each other too.
    /// </summary>
    private async Task<HashSet<string>> ExternalIdsHeldAsync(IReadOnlyList<Repository> repositories, ProviderInstance instance, CancellationToken cancellationToken)
    {
        var ids = repositories.Select(r => r.ExternalId).Distinct().ToList();
        var movingIds = repositories.Select(r => r.Id).ToList();

        var held = await _db.Repository.AsNoTracking()
            .Where(r => r.ProviderInstanceId == instance.Id && r.DeletedDate == null && ids.Contains(r.ExternalId) && !movingIds.Contains(r.Id))
            .Select(r => r.ExternalId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return held.ToHashSet();
    }

    private static string AlreadyBound(Repository repository, ProviderInstance instance) =>
        $"'{repository.FullPath}' is already bound under '{instance.DisplayName}', and a repository is bound once per connection. Unbind one of the two first.";

    /// <summary>What a bind asks of a credential before any wire call (<see cref="EnsureCredentialCoversBindCapabilities"/>), for the hook the new connection's scope will register.</summary>
    private string? ScopeRefusal(Repository repository, Credential to, ProviderInstance instance)
    {
        try
        {
            _scopeChecker.EnsureCapability(to, instance.Provider, typeof(IRepositoryCatalogCapability));
            _scopeChecker.EnsureCapability(to, instance.Provider, RegistersPerRepository(instance) ? typeof(IWebhookRegistrationCapability) : typeof(IConnectionWebhookRegistrationCapability));

            return null;
        }
        catch (ProviderInsufficientScopeException ex)
        {
            return $"Credential '{to.DisplayName}' cannot take '{repository.FullPath}' over: {ex.Message}";
        }
    }

    /// <summary>
    /// The repository's hooks under the connection it leaves, retired as an unbind retires them — a Registered row is removed (it described a hook that is
    /// to be deleted), every other in-service row is cancelled so a registrar mid-call cannot complete behind the move — except that the
    /// provider is asked to delete the hooks after the commit, not under the transaction. Null when none names a hook at the provider.
    /// </summary>
    private async Task<RetiredRemoteHooks?> RetireHooksAsync(Repository repository, ProviderInstance instance, Credential to, CancellationToken cancellationToken)
    {
        var hooks = await LoadAllWebhooksAsync(repository.Id, cancellationToken).ConfigureAwait(false);
        var atProvider = hooks.Where(h => !string.IsNullOrEmpty(h.ExternalId)).Select(h => h.ExternalId!).ToList();

        await CancelNonTerminalWebhooksAsync(repository.Id, cancellationToken).ConfigureAwait(false);

        _db.RepositoryWebhook.RemoveRange(hooks.Where(h => h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Registered));

        return atProvider.Count == 0 ? null : new RetiredRemoteHooks(instance, to, repository.ToRemoteRepository(), atProvider);
    }

    /// <summary>
    /// What delivers the repository's events at its new connection, the way a bind stages it. Under connection-wide scope the owner hook is flushed
    /// before the next repository asks whether one covers its owner: a hook only in the change tracker answers no, and a second would hit the
    /// owner's unique index — exactly why a bind flushes it.
    /// </summary>
    private async Task<RehomeStaged> StageCoverageAsync(Repository repository, ProviderInstance instance, Credential to, CancellationToken cancellationToken)
    {
        if (RegistersPerRepository(instance))
        {
            var webhook = NewPendingWebhook(instance.Provider, repository.Id, Guid.NewGuid(), NewWebhookSecret());

            _db.RepositoryWebhook.Add(webhook);

            return new RehomeStaged { RepositoryHookId = webhook.Id };
        }

        var groupHookId = await _connectionProvisioner.EnsureForOwnerAsync(instance, to.Id, repository.NamespacePath, cancellationToken).ConfigureAwait(false);

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new RehomeStaged { ConnectionHookId = groupHookId };
    }

    private async Task DeleteRetiredAtProviderAsync(RetiredRemoteHooks retired, CancellationToken cancellationToken)
    {
        if (!_registry.TryGet<IWebhookRegistrationCapability>(retired.Instance.Provider, out var capability)) return;

        var context = new ProviderContext(retired.Instance, retired.Credential);

        foreach (var externalId in retired.HookExternalIds)
        {
            try
            {
                await capability!.DeleteWebhookAsync(context, retired.Repository, externalId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // A hook that cannot be deleted is one the operator removes by hand; the local row is already gone, so a delivery on it is refused.
                _logger.LogWarning(ex, "Could not delete remote webhook {WebhookExternalId} of repository {RepositoryPath} after it moved to connection {ProviderInstanceId}", externalId, retired.Repository.FullPath, retired.Instance.Id);
            }
        }
    }
}

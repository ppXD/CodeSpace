using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>The same lift-the-error and revive-the-hooks an adoption performs, for a credential that stays where it is.</summary>
public sealed partial class CredentialSuccessionService : ICredentialBindingReviver
{
    public async Task<BindingsRevived> ReviveBindingsAsync(Credential credential, CancellationToken cancellationToken)
    {
        var own = new List<Guid> { credential.Id };

        var repositories = await LoadBoundRepositoriesAsync(credential.TeamId, new[] { credential.ProviderInstanceId }, own, cancellationToken).ConfigureAwait(false);
        var hooks = await LoadBoundConnectionWebhooksAsync(credential, own, cancellationToken).ConfigureAwait(false);

        var revived = new BindingsRevived { RepositoryCount = repositories.Count(r => r.Status == RepositoryStatus.Error), ConnectionWebhookCount = hooks.Count(h => Revivable.Contains(h.RegistrationStatus)) };

        foreach (var repository in repositories) Revive(repository);
        foreach (var hook in hooks) Revive(hook);

        await DispatchAfterCommitAsync(hooks.Where(h => h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Pending).Select(h => h.Id).ToList(), cancellationToken).ConfigureAwait(false);

        return revived;
    }
}

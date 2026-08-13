using CodeSpace.Messages.Dtos.Repositories;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// The operator-facing surface of a repository's webhooks: what they are, what went wrong, the
/// secret that authenticates a delivery, and the one recovery action.
///
/// <para>Distinct from <see cref="IWebhookIngestionService"/> (which is the receiver) and from
/// <c>Registration.IRepositoryWebhookRegistrar</c> (which is the worker). This one exists for a
/// person looking at a repository and asking why it is not firing.</para>
///
/// <para>Tenancy is the caller's: every method is keyed by repositoryId and the requests that
/// reach it carry <c>IRequireRepositoryAccess</c>, which resolves the repository to its team
/// before any handler runs.</para>
/// </summary>
public interface IRepositoryWebhookService
{
    /// <summary>Every webhook on the repository, oldest first, each with its full attempt timeline. Never carries the secret.</summary>
    Task<IReadOnlyList<RepositoryWebhookDetail>> ListAsync(Guid repositoryId, CancellationToken cancellationToken);

    /// <summary>Decrypt one webhook's signing secret and record that it was taken, and by whom.</summary>
    Task<RepositoryWebhookSecret> RevealSecretAsync(Guid repositoryId, Guid webhookId, CancellationToken cancellationToken);

    /// <summary>
    /// Revive a Failed or DeadLettered registration and hand it to the dispatcher — the reconciler's
    /// path, run now. Throws when the webhook is in any other state. The returned row reads Pending:
    /// the dispatch is deferred to after this command's transaction commits.
    /// </summary>
    Task<RepositoryWebhookDetail> RetryRegistrationAsync(Guid repositoryId, Guid webhookId, CancellationToken cancellationToken);
}

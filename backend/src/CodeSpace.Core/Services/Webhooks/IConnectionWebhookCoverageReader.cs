using CodeSpace.Messages.Dtos.Repositories;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// Which connection-scoped hook covers a repository, in the vocabulary the Webhook tab already
/// speaks.
///
/// <para>A sibling of <see cref="IRepositoryWebhookService"/> rather than a method on it, for the
/// reason <see cref="IRejectedDeliveryReader"/> is one: that service reads <c>repository_webhook</c>
/// and administers rows the repository OWNS — list, reveal its secret, retry its registration — and
/// none of those verbs apply here. A connection hook belongs to the connection: it covers many
/// repositories, its secret is not this repository's to hand out, and retrying it is a connection
/// operation. Widening the repository service to return it would put all of that behind an
/// interface whose other three methods would then have to answer "which grain?" one by one.</para>
///
/// <para>Read-only by construction. The rows come from the connection-scoped registration path.</para>
/// </summary>
public interface IConnectionWebhookCoverageReader
{
    /// <summary>
    /// What covers this repository. Always answers — under per-repository scope it says so, which is
    /// what lets the tab render one thing rather than guessing from an empty list.
    /// </summary>
    Task<RepositoryWebhookCoverage> GetForRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken);
}

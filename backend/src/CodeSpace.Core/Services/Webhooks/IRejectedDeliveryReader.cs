using CodeSpace.Messages.Dtos.Repositories;

namespace CodeSpace.Core.Services.Webhooks;

/// <summary>
/// The deliveries that DID arrive and were refused.
///
/// <para>A sibling of <see cref="IRepositoryWebhookService"/> rather than a method on it, because
/// the two read different tables to answer different questions. That one reads
/// <c>repository_webhook</c> and answers "does the hook exist and did we manage to create it";
/// this one reads <c>workflow_run_request</c> and answers "the hook exists and the provider is
/// sending — what happens to those". Widening the webhook service to cover both would put the
/// run-request journal behind an interface named for webhook administration, and every later
/// reader of refusals (a team-wide view, a run's own provenance) would have to go through it.</para>
///
/// <para>Read-only by construction: nothing here writes. The rows come from
/// <c>IIngestionAuditor</c>, which is the only thing that creates them.</para>
///
/// <para>Tenancy is resolved from the repository itself rather than from the request's team header,
/// so the answer is the same whether the caller came through the authorization pipeline or is a
/// background job that bypasses it.</para>
/// </summary>
public interface IRejectedDeliveryReader
{
    /// <summary>Recent refusals for one repository, newest first, capped — see <see cref="RepositoryRejectedDeliveries.Cap"/>.</summary>
    Task<RepositoryRejectedDeliveries> ListForRepositoryAsync(Guid repositoryId, CancellationToken cancellationToken);
}

using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// The deliveries this repository refused, newest first and capped — the Webhook tab's second read.
/// Membership enforced via <see cref="IRequireRepositoryAccess"/>, same as the webhook list.
///
/// <para>No filters and no paging on purpose. The question is "is anything being thrown away right
/// now", and the newest few answer it; a reader who needs the whole history of an unreachable
/// instance is reading the run-request journal, not a repository tab.</para>
/// </summary>
public sealed record ListRepositoryRejectedDeliveriesQuery : IQuery<RepositoryRejectedDeliveries>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}

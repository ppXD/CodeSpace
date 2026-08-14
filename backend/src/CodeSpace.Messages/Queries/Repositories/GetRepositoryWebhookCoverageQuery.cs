using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// What covers this repository's events when it has no hook of its own — the Webhook tab's third
/// read. Membership enforced via <see cref="IRequireRepositoryAccess"/>, same as the other two.
/// </summary>
public sealed record GetRepositoryWebhookCoverageQuery : IQuery<RepositoryWebhookCoverage>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}

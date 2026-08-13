using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Queries.Repositories;

/// <summary>
/// Every webhook on a repository with its attempt timeline — the Webhook tab's read.
/// Membership enforced via <see cref="IRequireRepositoryAccess"/>; the secret is not in the
/// answer and has its own endpoint.
/// </summary>
public sealed record ListRepositoryWebhooksQuery : IQuery<IReadOnlyList<RepositoryWebhookDetail>>, IRequireRepositoryAccess
{
    public required Guid RepositoryId { get; init; }
}

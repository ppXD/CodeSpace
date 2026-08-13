using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Repositories;

/// <summary>
/// Put a Failed or DeadLettered registration back in the queue now, without waiting for backoff.
///
/// <para>The reconciler already revives Failed rows once <c>next_attempt_at</c> elapses; it never
/// touches DeadLettered ones, which is the state an operator is actually looking at when they have
/// just fixed the credential. This is that same revival, on demand, and from either state.</para>
///
/// <para>Gated on <see cref="TeamPermissions.ReposManage"/>: it spends the repository's credential
/// on a provider-side write, which is the power binding a repository reserves.</para>
///
/// <para>Returns the webhook as it stands after the revival — Pending, because the dispatch is
/// deferred to after this command commits — so the tab can repaint the row it just acted on
/// without a second read.</para>
/// </summary>
public sealed record RetryRepositoryWebhookRegistrationCommand : ICommand<RepositoryWebhookDetail>, IRequireRepositoryAccess, IRequireTeamPermission
{
    public required Guid RepositoryId { get; init; }
    public required Guid WebhookId { get; init; }

    public string RequiredPermission => TeamPermissions.ReposManage;
}

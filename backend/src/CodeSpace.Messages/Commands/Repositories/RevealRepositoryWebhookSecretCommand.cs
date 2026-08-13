using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Repositories;

/// <summary>
/// Decrypt and hand back one webhook's signing secret.
///
/// <para>A command rather than a query, though it reads: whoever receives this value can forge a
/// signed delivery for the repository, so it is an ACTION with a blast radius and it leaves a log
/// line naming who took it. That is also why it carries a permission — queries in this system are
/// gated by membership alone, and membership is the wrong bar for handing out a credential.</para>
///
/// <para><see cref="TeamPermissions.ReposManage"/> is the same capability that binds and unbinds a
/// repository. Reading this secret is equivalent to being able to speak to the repository as its
/// provider, so it belongs at the tier that decides which repositories the team has at all.</para>
/// </summary>
public sealed record RevealRepositoryWebhookSecretCommand : ICommand<RepositoryWebhookSecret>, IRequireRepositoryAccess, IRequireTeamPermission
{
    public required Guid RepositoryId { get; init; }
    public required Guid WebhookId { get; init; }

    public string RequiredPermission => TeamPermissions.ReposManage;
}

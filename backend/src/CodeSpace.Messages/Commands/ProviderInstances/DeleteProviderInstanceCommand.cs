using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.ProviderInstances;

/// <summary>
/// Soft-delete a provider instance. By default refuses when any active repository is
/// still bound through this instance — deleting would orphan webhooks and surprise the
/// team with broken event ingestion.
///
/// Set <see cref="Force"/> = true to cascade-unbind every bound repository first (best-
/// effort remote webhook delete via any active credential, then soft-delete the repo
/// rows), then proceed to revoke credentials and soft-delete the provider. This is the
/// "Unbind all and remove" one-click path the UI offers when the operator explicitly
/// confirms the impact.
///
/// The connection's group hooks are deleted at the provider first, best effort, while the
/// tokens that registered them still work; the hook rows it keeps are out of service and lose
/// their secrets. Active credentials are then retired in the same transaction — token blanked,
/// Revoked, soft-deleted — and the instance's OAuth client secret is blanked with it, so nothing
/// the connection held stays decryptable. Each OAuth grant is then revoked at the provider, best
/// effort, after the transaction commits.
/// </summary>
public sealed record DeleteProviderInstanceCommand : ICommand<DeleteProviderInstanceResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ReposManage;

    public required Guid ProviderInstanceId { get; init; }

    /// <summary>When true, cascade-unbind every bound repository before deleting. Default false (refuse if any repo is bound).</summary>
    public bool Force { get; init; }
}

public sealed record DeleteProviderInstanceResult
{
    public required Guid ProviderInstanceId { get; init; }
    public required int UnboundRepositoryCount { get; init; }
    public required int RevokedCredentialCount { get; init; }
}

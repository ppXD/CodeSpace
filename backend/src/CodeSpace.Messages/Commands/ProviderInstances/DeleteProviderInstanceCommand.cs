using CodeSpace.Messages.Authorization;
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
/// Active credentials are always cascade-revoked in the same transaction — they're
/// useless once the provider is gone, and leaving them as Active would lie to the UI.
/// </summary>
public sealed record DeleteProviderInstanceCommand : ICommand<DeleteProviderInstanceResult>, IRequireTeamMembership
{
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

using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Credentials;

/// <summary>
/// Move EVERY live repository and group hook still bound through a dead credential onto another credential, in one
/// action — the recovery for a Disconnect or a departure that nothing carried forward. The per-repository
/// <c>RelinkRepositoryCredentialCommand</c> fixes one repository and never touches a group hook; a credential holding
/// forty of them and a hook was forty clicks and a hook that stayed dead.
///
/// <para>The source must be dead — Revoked, which includes a departed member's token the team retired and soft-deleted.
/// Taking bindings off a live credential is a Disconnect with <c>MoveBindingsToCredentialId</c>. The target must be a live
/// credential of the same provider instance — or of another connection of the team to the same host, which takes the repositories to its
/// connection with it (see <c>RelinkRepositoryCredentialCommand</c>; a group hook is keyed to its connection and stays) — that is the CALLER's own or
/// the team's: a teammate's personal token is refused, because the repositories would then act as that person. Before anything is written the target is asked to
/// read every repository it would take (the provider's own lookup, as a bind does); one it cannot read — the provider
/// answered 401, 403 or 404, or the token's scope falls short — refuses the whole move and names them (onto another connection, one it finds at another
/// path is refused too), so a relink never
/// trades a loud failure for a quiet one. A provider that does not answer (an outage, a rate limit) says nothing about the
/// target: that is a retryable refusal that blames nobody, and nothing is moved. The check is one call per repository and
/// bounded: more than 100 repositories on one credential are refused before any is read, and are re-linked a few at a time.</para>
///
/// <para>Needs both <see cref="TeamPermissions.ReposManage"/> (declared here) and
/// <see cref="TeamPermissions.CredentialsManage"/> (asserted by the service). The credential-keyed marker is not used for
/// the source: it refuses a soft-deleted credential, which is exactly the one this command exists for. The service loads
/// both credentials by (id, header team) instead.</para>
/// </summary>
public sealed record RelinkCredentialBindingsCommand : ICommand<RelinkCredentialBindingsResult>, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ReposManage;

    /// <summary>Set by the controller from the route segment via `command with { FromCredentialId = ... }`. Non-required so System.Text.Json doesn't 400-fail when the body omits it (URL is authoritative).</summary>
    public Guid FromCredentialId { get; init; }

    public required Guid ToCredentialId { get; init; }
}

public sealed record RelinkCredentialBindingsResult
{
    public required Guid FromCredentialId { get; init; }
    public required Guid ToCredentialId { get; init; }

    /// <summary>Repositories re-pointed — and, where the dead credential's disconnect had turned them Error, returned to Active.</summary>
    public required int MovedRepositoryCount { get; init; }

    /// <summary>Group hooks re-pointed; a failed or dead-lettered one is revived and handed to the registrar.</summary>
    public required int MovedConnectionWebhookCount { get; init; }
}

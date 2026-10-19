using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Repositories;

/// <summary>
/// Replace a repository's credential pointer. Used after a credential disconnect to point
/// the repo at another active credential of the same provider — restores API access
/// without re-binding the repo. The new credential MUST belong to the same
/// <c>ProviderInstance</c> as the repo, or to another connection of the team to the SAME host (same provider
/// and API root — a team that signs in to one GitLab through two OAuth applications holds two connections to it);
/// a GitHub repo can't borrow a GitLab credential, nor a repo borrow a credential of another host.
/// On success the repo flips from Error → Active and LastError is cleared.
///
/// <para>Within one connection nothing else changes: webhook ingestion is unaffected — webhooks authenticate via
/// the registered secret, not the credential — so this only fixes the API-call path. A credential of ANOTHER
/// connection moves the repository to that connection with it (the repository is the same row: its id, project links and
/// history stay), after the credential has been asked for the repository by its id and found it at the path CodeSpace has, and only if it is the
/// caller's own or a team token, covers the scopes the new connection's hook needs, and the connection does not already hold the project. The
/// repository's hooks are registered again through the new connection and the old ones retired.</para>
///
/// <para><see cref="TeamPermissions.ReposManage"/>: choosing which credential a repository speaks through is part of
/// binding it. The repository marker alone let a Viewer point a repository at any credential in the team.</para>
/// </summary>
public sealed record RelinkRepositoryCredentialCommand : ICommand<Unit>, IRequireRepositoryAccess, IRequireTeamPermission
{
    public string RequiredPermission => TeamPermissions.ReposManage;

    /// <summary>Set by the controller from the route segment via `command with { RepositoryId = ... }`. Non-required so System.Text.Json doesn't 400-fail when the body omits it (URL is authoritative).</summary>
    public Guid RepositoryId { get; init; }
    public required Guid NewCredentialId { get; init; }
}

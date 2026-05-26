using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Mediation;
using MediatR;

namespace CodeSpace.Messages.Commands.Repositories;

/// <summary>
/// Replace a repository's credential pointer. Used after a credential disconnect to point
/// the repo at another active credential of the same provider — restores API access
/// without re-binding the repo. The new credential MUST belong to the same
/// <c>ProviderInstance</c> as the repo (a GitHub repo can't borrow a GitLab credential).
/// On success the repo flips from Error → Active and LastError is cleared.
///
/// Webhook ingestion is unaffected — webhooks authenticate via the registered secret,
/// not the credential — so this command only fixes the API-call path.
/// </summary>
public sealed record RelinkRepositoryCredentialCommand : ICommand<Unit>, IRequireRepositoryAccess
{
    /// <summary>Set by the controller from the route segment via `command with { RepositoryId = ... }`. Non-required so System.Text.Json doesn't 400-fail when the body omits it (URL is authoritative).</summary>
    public Guid RepositoryId { get; init; }
    public required Guid NewCredentialId { get; init; }
}

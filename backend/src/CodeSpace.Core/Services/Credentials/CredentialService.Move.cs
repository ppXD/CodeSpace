using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>Where a Disconnect sends what was bound through the credential: the one the caller names, or — when the owner disconnects their own — the one their other connection makes obvious.</summary>
public sealed partial class CredentialService
{
    private async Task<Credential?> ResolveSuccessorAsync(Credential credential, Guid? requestedId, CancellationToken cancellationToken)
    {
        if (requestedId is Guid id) return await LoadRequestedSuccessorAsync(credential, id, cancellationToken).ConfigureAwait(false);

        return await FindImpliedSuccessorAsync(credential, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// The credential the caller chose. It must be another connection of the SAME owner — an Admin disconnecting a teammate's
    /// credential may send its bindings to that teammate's other connection, never to their own — and what the move itself
    /// requires of any target. Refused here, before anything is revoked.
    /// </summary>
    private async Task<Credential> LoadRequestedSuccessorAsync(Credential credential, Guid id, CancellationToken cancellationToken)
    {
        var successor = await _db.Credential.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id && c.TeamId == credential.TeamId && c.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"credential {id} not found");

        if (!RelinkSuggestionRanking.HaveTheSameOwner(credential, successor))
            throw new InvalidOperationException($"Credential '{successor.DisplayName}' belongs to someone else. Bindings move on Disconnect only to another connection of the same owner — or, for a team token, another team token.");

        if (await _succession.RefusalAsync(credential, successor, cancellationToken).ConfigureAwait(false) is { } refusal)
            throw new InvalidOperationException(refusal);

        return successor;
    }

    /// <summary>
    /// Nobody was named. When the person whose connection this is disconnects it themselves and holds exactly one other live
    /// connection on the same instance, that is where reconnecting would have sent it — so it is where it goes, and
    /// connect-then-disconnect ends where disconnect-then-connect does (<see cref="ICredentialSuccessionService.FindOnlyOtherConnectionAsync"/>
    /// holds the rule, and why it stops there). Nothing is implied for anyone else's, a credential already disconnected, or a
    /// team-service token, and a default that would be refused is no default: Disconnect itself must never fail because of it.
    /// </summary>
    private async Task<Credential?> FindImpliedSuccessorAsync(Credential credential, CancellationToken cancellationToken)
    {
        if (_currentUser.Id is not Guid callerId) return null;

        return await _succession.FindOnlyOtherConnectionAsync(credential, callerId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<BindingsMoved> MoveBindingsAsync(Credential credential, Credential? successor, CancellationToken cancellationToken)
    {
        if (successor == null) return BindingsMoved.None;

        return await _succession.MoveBindingsAsync(credential, successor, cancellationToken).ConfigureAwait(false);
    }
}

using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// Taking a disconnected credential with nothing on it out of the team's list. The row ends as a departed member's retired credential
/// does — soft-deleted, Revoked, its token already blanked by the Disconnect — so no list shows it and nothing can revive it. A write
/// that reads, judges and then writes would lose to a reconnect landing in between, so the judgement is made again by the write itself.
/// </summary>
public sealed partial class CredentialService
{
    public async Task<Guid> RemoveDisconnectedAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await LoadRemovableAsync(credentialId, cancellationToken).ConfigureAwait(false);

        await EnsureMayBeRemovedAsync(credential, cancellationToken).ConfigureAwait(false);
        await SoftDeleteWhileStillDisconnectedAsync(credential, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("Removed disconnected credential {CredentialId} in team {TeamId}: nothing leaned on it", credential.Id, credential.TeamId);

        return credential.Id;
    }

    /// <summary>The team's credential by id. One already removed is no longer anyone's to remove: not found, as it reads everywhere else.</summary>
    private async Task<Credential> LoadRemovableAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var credential = await LoadTeamCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);

        if (credential.DeletedDate != null)
            throw new KeyNotFoundException($"credential {credentialId} not found");

        return credential;
    }

    /// <summary>What leans on it is read the way a move would carry it (<see cref="ICredentialRelinkService.GetDependentsAsync"/>), so a removal and a relink agree on what a credential's dependents are.</summary>
    private async Task EnsureMayBeRemovedAsync(Credential credential, CancellationToken cancellationToken)
    {
        var dependents = await _relink.GetDependentsAsync(credential.Id, cancellationToken).ConfigureAwait(false);

        if (CredentialRemoval.Refusal(credential, dependents) is { } refusal)
            throw new InvalidOperationException(refusal);
    }

    /// <summary>
    /// The one write, conditional on the row still being disconnected and still there: a reconnect that landed after the checks sets it Active, and this
    /// then matches nothing — refused, never an overwrite of the token the person was just told was stored. <c>ExecuteUpdate</c> skips the audit hook, so the
    /// modifier is stamped here.
    /// </summary>
    private async Task SoftDeleteWhileStillDisconnectedAsync(Credential credential, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var by = _currentUser.Id!.Value;

        var stillDisconnected = _db.Credential.Where(c => c.Id == credential.Id && c.Status == CredentialStatus.Revoked && c.DeletedDate == null);

        var removed = await stillDisconnected.ExecuteUpdateAsync(s => s.SetProperty(c => c.DeletedDate, now).SetProperty(c => c.LastModifiedDate, now).SetProperty(c => c.LastModifiedBy, by), cancellationToken).ConfigureAwait(false);

        if (removed == 0)
            throw new InvalidOperationException($"Credential '{credential.DisplayName}' was reconnected or changed while it was being removed, so nothing was removed. Look at it again.");
    }
}

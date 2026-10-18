using CodeSpace.Core.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// The third door onto the same move: a person (or an Admin for them) names where a credential's bindings go, instead of
/// waiting for a connect or the repair sweep to find a successor. The floor every move obeys lives here; who may ask for
/// which target is each caller's own rule.
/// </summary>
public sealed partial class CredentialSuccessionService
{
    public async Task<string?> RefusalAsync(Credential from, Credential to, CancellationToken cancellationToken)
    {
        if (from.Id == to.Id) return "A credential cannot take over its own bindings.";

        if (to.TeamId != from.TeamId || to.DeletedDate != null) return $"Credential {to.Id} not found or no longer active.";

        if (to.ProviderInstanceId != from.ProviderInstanceId) return $"Credential '{to.DisplayName}' is for a different provider instance. Pick a credential connected to the same provider as '{from.DisplayName}'.";

        if (to.Status != CredentialStatus.Active) return $"Credential '{to.DisplayName}' is {to.Status} — pick an Active credential.";

        return await LackOfStandingAsync(to, cancellationToken).ConfigureAwait(false);
    }

    public async Task<Credential?> FindOnlyOtherConnectionAsync(Credential from, Guid callerId, CancellationToken cancellationToken)
    {
        if (!IsTheCallersOwnLiveConnection(from, callerId)) return null;

        var others = await _db.Credential.AsNoTracking()
            .Where(c => c.TeamId == from.TeamId && c.ProviderInstanceId == from.ProviderInstanceId && c.OwnerUserId == from.OwnerUserId && c.Ownership == CredentialOwnership.Personal)
            .Where(c => c.Status == CredentialStatus.Active && c.DeletedDate == null && c.Id != from.Id)
            .Take(2).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (others.Count != 1) return null;

        return await RefusalAsync(from, others[0], cancellationToken).ConfigureAwait(false) is null ? others[0] : null;
    }

    private static bool IsTheCallersOwnLiveConnection(Credential from, Guid callerId) =>
        RelinkSuggestionRanking.IsLive(from) && from.Ownership == CredentialOwnership.Personal && from.OwnerUserId == callerId;

    public async Task<BindingsMoved> MoveBindingsAsync(Credential from, Credential to, CancellationToken cancellationToken)
    {
        if (await RefusalAsync(from, to, cancellationToken).ConfigureAwait(false) is { } refusal)
            throw new InvalidOperationException(refusal);

        var adopted = await AdoptBindingsAsync(to, new List<Guid> { from.Id }, cancellationToken).ConfigureAwait(false);

        await DispatchAfterCommitAsync(adopted.RevivedHookIds, cancellationToken).ConfigureAwait(false);

        return new BindingsMoved { RepositoryCount = adopted.RepositoryCount, ConnectionWebhookCount = adopted.ConnectionWebhookCount };
    }

    /// <summary>
    /// A person's credential held by someone with no standing in the team (<see cref="TeamStanding"/>) is one the departed-
    /// token sweep retires: whatever it took in would be stranded again within the tick. A team-service credential belongs
    /// to the team and has no person to ask about.
    /// </summary>
    private async Task<string?> LackOfStandingAsync(Credential to, CancellationToken cancellationToken)
    {
        if (to.Ownership != CredentialOwnership.Personal || to.OwnerUserId is not Guid ownerId) return null;

        if (await TeamStanding.HeldAsync(_db, to.TeamId, ownerId, cancellationToken).ConfigureAwait(false)) return null;

        return $"Credential '{to.DisplayName}' belongs to someone who no longer has standing in this team, so it would be retired and the repositories stranded again.";
    }
}

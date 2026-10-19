using CodeSpace.Core.Authorization;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.ProviderInstances;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>
/// The third door onto the same move: a person (or an Admin for them) names where a credential's bindings go, instead of
/// waiting for a connect or the repair sweep to find a successor. The floor every move obeys lives here; who may ask for
/// which target is each caller's own rule. The target may belong to another connection to the same host
/// (<see cref="ProviderInstanceHost"/>), and then a repository that moves onto it moves to that connection too
/// (<see cref="IRepositoryRehoming"/>).
/// </summary>
public sealed partial class CredentialSuccessionService
{
    public async Task<string?> RefusalAsync(Credential from, Credential to, CancellationToken cancellationToken)
    {
        if (from.Id == to.Id) return "A credential cannot take over its own bindings.";

        if (to.TeamId != from.TeamId || to.DeletedDate != null) return $"Credential {to.Id} not found or no longer active.";

        if (!await ReachesTheSameHostAsync(from, to, cancellationToken).ConfigureAwait(false)) return $"Credential '{to.DisplayName}' is for a different provider instance. Pick a credential connected to the same provider as '{from.DisplayName}'.";

        if (to.Status != CredentialStatus.Active) return $"Credential '{to.DisplayName}' is {to.Status} — pick an Active credential.";

        return await LackOfStandingAsync(to, cancellationToken).ConfigureAwait(false) ?? await RehomingRefusalAsync(from, to, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Most refusals worded in one message — enough to act on, short enough to read.</summary>
    private const int NamedRefusalLimit = 3;

    /// <summary>
    /// Whether the repositories bound through <paramref name="from"/> may move onto a credential of another connection (<see cref="IRepositoryRehoming.RefusalsAsync"/>): one
    /// that would not is a reason to refuse the whole move, since a person who named the target asked for everything to go there. Nothing to ask when the credentials
    /// share a connection.
    /// </summary>
    private async Task<string?> RehomingRefusalAsync(Credential from, Credential to, CancellationToken cancellationToken)
    {
        if (to.ProviderInstanceId == from.ProviderInstanceId) return null;

        var crossing = await _db.Repository.AsNoTracking()
            .Where(r => r.CredentialId == from.Id && r.TeamId == from.TeamId && r.ProviderInstanceId == from.ProviderInstanceId && r.DeletedDate == null)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (crossing.Count == 0) return null;

        var refused = await _rehoming.Value.RefusalsAsync(crossing, to, cancellationToken).ConfigureAwait(false);

        if (refused.Count == 0) return null;

        return $"{refused.Count} of the {crossing.Count} repositories bound through '{from.DisplayName}' cannot move onto '{to.DisplayName}'. {string.Join(" ", refused.Values.Take(NamedRefusalLimit))}";
    }

    /// <summary>The same connection, or another live connection of the team to the same host — the one definition, <see cref="ProviderInstanceHost.AreInterchangeable"/>.</summary>
    private async Task<bool> ReachesTheSameHostAsync(Credential from, Credential to, CancellationToken cancellationToken) =>
        await ProviderInstanceHost.AreInterchangeableAsync(_db, from.TeamId, from.ProviderInstanceId, to.ProviderInstanceId, cancellationToken).ConfigureAwait(false);

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

        EnsureNothingWasLeftBehind(from, to, adopted);

        await DispatchAfterCommitAsync(adopted, cancellationToken).ConfigureAwait(false);

        return new BindingsMoved { RepositoryCount = adopted.RepositoryCount, ConnectionWebhookCount = adopted.ConnectionWebhookCount };
    }

    /// <summary>
    /// A move a person asked for moves everything or nothing: <see cref="RefusalAsync"/> has already refused what it can know, so a repository left behind here is
    /// one that changed since — it is thrown, and the caller's unit of work rolls the rest of the move back.
    /// </summary>
    private static void EnsureNothingWasLeftBehind(Credential from, Credential to, Adopted adopted)
    {
        if (adopted.Refusals.Count == 0) return;

        throw new InvalidOperationException($"{adopted.Refusals.Count} repositories bound through '{from.DisplayName}' cannot move onto '{to.DisplayName}'. Nothing was moved. {string.Join(" ", adopted.Refusals.Take(NamedRefusalLimit))}");
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

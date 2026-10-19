using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

public sealed partial class CredentialRelinkService
{
    public async Task<IReadOnlyList<RelinkSuggestion>> GetSuggestionsAsync(Guid credentialId, CancellationToken cancellationToken)
    {
        var from = await LoadTeamCredentialAsync(credentialId, cancellationToken).ConfigureAwait(false);
        var callerId = _currentUser.Id ?? throw TenantAccessDeniedException.NotAuthenticated(from.TeamId);

        var reach = await ProviderInstanceHost.InterchangeableIdsAsync(_db, from.ProviderInstance, cancellationToken).ConfigureAwait(false);
        var reachSet = reach.ToHashSet();

        var candidates = await LoadCandidatesAsync(from, reach, callerId, cancellationToken).ConfigureAwait(false);
        var accepted = await KeepWhatTheMoveAcceptsAsync(from, candidates, callerId, reachSet, cancellationToken).ConfigureAwait(false);

        return RelinkSuggestionRanking.Rank(from, accepted, callerId, reachSet);
    }

    /// <summary>The live credentials of the team, on the credential's instance or another connection to the same host, that could be on either list: the caller's own, the team's, and the credential's owner's — the last being whom a Disconnect of a live credential may hand its bindings to.</summary>
    private async Task<List<Credential>> LoadCandidatesAsync(Credential from, IReadOnlyList<Guid> reach, Guid callerId, CancellationToken cancellationToken) =>
        await _db.Credential.AsNoTracking().Include(c => c.ProviderInstance)
            .Where(c => c.TeamId == from.TeamId && reach.Contains(c.ProviderInstanceId) && c.Id != from.Id)
            .Where(c => c.Status == CredentialStatus.Active && c.DeletedDate == null)
            .Where(c => c.OwnerUserId == callerId || c.OwnerUserId == from.OwnerUserId || c.Ownership == CredentialOwnership.TeamService)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Only the candidates the rules in <see cref="RelinkSuggestionRanking"/> let through, and of those the ones the move itself would not refuse — the holder of a person's credential must still stand in the team.</summary>
    private async Task<List<Credential>> KeepWhatTheMoveAcceptsAsync(Credential from, List<Credential> candidates, Guid callerId, IReadOnlySet<Guid> reach, CancellationToken cancellationToken)
    {
        var accepted = new List<Credential>();

        foreach (var candidate in candidates.Where(c => RelinkSuggestionRanking.Qualifies(from, c, callerId, reach)))
        {
            if (await _succession.RefusalAsync(from, candidate, cancellationToken).ConfigureAwait(false) is null) accepted.Add(candidate);
        }

        return accepted;
    }
}

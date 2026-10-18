using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Entities;
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

        var candidates = await LoadCandidatesAsync(from, callerId, cancellationToken).ConfigureAwait(false);
        var accepted = await KeepWhatTheMoveAcceptsAsync(from, candidates, callerId, cancellationToken).ConfigureAwait(false);

        return RelinkSuggestionRanking.Rank(from, accepted, callerId);
    }

    /// <summary>The live credentials of the team and instance that could be on either list: the caller's own, the team's, and the credential's owner's — the last being whom a Disconnect of a live credential may hand its bindings to.</summary>
    private async Task<List<Credential>> LoadCandidatesAsync(Credential from, Guid callerId, CancellationToken cancellationToken) =>
        await _db.Credential.AsNoTracking()
            .Where(c => c.TeamId == from.TeamId && c.ProviderInstanceId == from.ProviderInstanceId && c.Id != from.Id)
            .Where(c => c.Status == CredentialStatus.Active && c.DeletedDate == null)
            .Where(c => c.OwnerUserId == callerId || c.OwnerUserId == from.OwnerUserId || c.Ownership == CredentialOwnership.TeamService)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>Only the candidates the rules in <see cref="RelinkSuggestionRanking"/> let through, and of those the ones the move itself would not refuse — the holder of a person's credential must still stand in the team.</summary>
    private async Task<List<Credential>> KeepWhatTheMoveAcceptsAsync(Credential from, List<Credential> candidates, Guid callerId, CancellationToken cancellationToken)
    {
        var accepted = new List<Credential>();

        foreach (var candidate in candidates.Where(c => RelinkSuggestionRanking.Qualifies(from, c, callerId)))
        {
            if (await _succession.RefusalAsync(from, candidate, cancellationToken).ConfigureAwait(false) is null) accepted.Add(candidate);
        }

        return accepted;
    }
}

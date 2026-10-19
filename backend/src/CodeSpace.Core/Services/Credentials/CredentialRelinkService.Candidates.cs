using CodeSpace.Core.Authorization;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>The dialog's read side: what a repository could be pointed at, across every connection of the team to the host it lives on.</summary>
public sealed partial class CredentialRelinkService
{
    public async Task<IReadOnlyList<RelinkSuggestion>> GetCandidatesAsync(Guid providerInstanceId, Guid? leavingCredentialId, CancellationToken cancellationToken)
    {
        var teamId = _currentTeam.Id ?? throw TenantAccessDeniedException.NoTeamSelected(_currentUser.Id, HeaderCurrentTeam.HeaderName);
        var callerId = _currentUser.Id ?? throw TenantAccessDeniedException.NotAuthenticated(teamId);

        var instance = await LoadTeamInstanceAsync(providerInstanceId, teamId, cancellationToken).ConfigureAwait(false);
        var leaving = leavingCredentialId is Guid id ? await LoadTeamCredentialAsync(id, cancellationToken).ConfigureAwait(false) : null;

        var reach = await ProviderInstanceHost.InterchangeableIdsAsync(_db, instance, cancellationToken).ConfigureAwait(false);
        var bindable = await LoadBindableAsync(teamId, reach, callerId, cancellationToken).ConfigureAwait(false);

        var ranked = RelinkSuggestionRanking.RankForRepository(instance.Id, leaving, bindable, callerId, _clock.GetUtcNow());

        return await WithStrandedActAsAsync(instance.Id, ranked, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// What a pick on another connection takes from the people who act as themselves: an identity belongs to a connection, so a repository that moves
    /// leaves behind everyone whose live identity is on the connection it leaves and not on the one it goes to. Named on each candidate that would, by the
    /// act-as reader's own predicate — the people the author picker offers today and would no longer. Nothing is read when no candidate is on another
    /// connection, so a team with one connection pays for nothing.
    /// </summary>
    private async Task<IReadOnlyList<RelinkSuggestion>> WithStrandedActAsAsync(Guid repositoryInstanceId, IReadOnlyList<RelinkSuggestion> ranked, CancellationToken cancellationToken)
    {
        var elsewhere = ranked.Select(r => r.ProviderInstanceId).Where(id => id != repositoryInstanceId).Distinct().ToList();

        if (elsewhere.Count == 0) return ranked;

        var here = await _actors.ListCandidatesOnInstanceAsync(repositoryInstanceId, cancellationToken).ConfigureAwait(false);

        if (here.Count == 0) return ranked;

        var stranded = new Dictionary<Guid, IReadOnlyList<string>>();

        foreach (var instanceId in elsewhere) stranded[instanceId] = await NamesNotOnAsync(here, instanceId, cancellationToken).ConfigureAwait(false);

        return ranked.Select(r => stranded.TryGetValue(r.ProviderInstanceId, out var names) ? r with { StrandedActAs = names } : r).ToList();
    }

    /// <summary>The names, among <paramref name="people"/>, of those with no live identity on <paramref name="instanceId"/>.</summary>
    private async Task<IReadOnlyList<string>> NamesNotOnAsync(IReadOnlyList<ActAsCandidateSummary> people, Guid instanceId, CancellationToken cancellationToken)
    {
        var there = (await _actors.ListCandidatesOnInstanceAsync(instanceId, cancellationToken).ConfigureAwait(false)).Select(c => c.UserId).ToHashSet();

        return people.Where(p => !there.Contains(p.UserId)).Select(p => p.Name).ToList();
    }

    /// <summary>The team's connected credentials on those connections that the caller may bind with: their own personal ones, and the team's service tokens. The ranking keeps the ones that can still authenticate (<see cref="RelinkSuggestionRanking.CanAuthenticate"/>).</summary>
    private async Task<List<Credential>> LoadBindableAsync(Guid teamId, IReadOnlyList<Guid> instanceIds, Guid callerId, CancellationToken cancellationToken) =>
        await _db.Credential.AsNoTracking().Include(c => c.ProviderInstance)
            .Where(c => c.TeamId == teamId && instanceIds.Contains(c.ProviderInstanceId) && c.Status == CredentialStatus.Active && c.DeletedDate == null)
            .Where(c => (c.Ownership == CredentialOwnership.Personal && c.OwnerUserId == callerId) || c.Ownership == CredentialOwnership.TeamService)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>The team's provider connection by id — not found for a missing id and for another team's alike.</summary>
    private async Task<ProviderInstance> LoadTeamInstanceAsync(Guid id, Guid teamId, CancellationToken cancellationToken) =>
        await _db.ProviderInstance.AsNoTracking().SingleOrDefaultAsync(p => p.Id == id && p.TeamId == teamId && p.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"provider instance {id} not found");
}

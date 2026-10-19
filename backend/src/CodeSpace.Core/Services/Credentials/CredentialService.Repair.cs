using CodeSpace.Core.Authorization;
using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials;

/// <summary>What the person listing may do about each credential — the answer a client shows its Reconnect and Replace token buttons by.</summary>
public sealed partial class CredentialService
{
    /// <summary>
    /// Says, for each listed credential, whether the caller may put it right in place: a sign-in reconnected or a pasted token replaced, by exactly the
    /// rules the endpoints behind those buttons apply (<see cref="CallerStanding"/>), so the list never offers what the server would refuse — a
    /// teammate's credential to someone without <c>credentials.manage</c>, another Admin's or an Owner's sign-in to an Admin, one whose person left.
    /// One read of the standing of every owner on the list, however long it is.
    /// </summary>
    private async Task<IReadOnlyList<CredentialSummary>> WithCallerRepairAsync(IReadOnlyList<CredentialSummary> summaries, Guid teamId, CancellationToken cancellationToken)
    {
        var role = await _membership.ResolveRoleAsync(teamId, cancellationToken).ConfigureAwait(false);
        var owners = summaries.Where(s => s.Ownership == CredentialOwnership.Personal && s.OwnerUserId != null).Select(s => s.OwnerUserId!.Value).Distinct().ToList();
        var standing = await TeamStanding.RolesActedOnAsync(_db, teamId, owners, cancellationToken).ConfigureAwait(false);

        return summaries.Select(s => s with { CallerMayRepair = MayRepair(role, s, standing) }).ToList();
    }

    private bool MayRepair(TeamRole role, CredentialSummary credential, IReadOnlyDictionary<Guid, TeamRole> standing) =>
        CallerStanding.Of(role, CredentialPerson.Of(credential.Ownership, credential.OwnerUserId, _currentUser.Id, standing)).MayRepair(CredentialHealthDerivation.IsStaticToken(credential.AuthType));
}

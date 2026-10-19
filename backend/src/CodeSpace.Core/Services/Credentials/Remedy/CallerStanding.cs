using CodeSpace.Core.Authorization;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials.Remedy;

/// <summary>
/// What the person asking may do about a provider failure, decided by the SAME rules the endpoints that do it apply — so a button
/// is enabled exactly when the request behind it would be accepted. Pure: the team role and who the credential belongs to are the only inputs.
///
/// <list type="bullet">
/// <item><c>repos.manage</c> (Admin and Owner): re-link a repository, edit a provider's address.</item>
/// <item>Replace a pasted token (<c>PUT /api/credentials/{id}/token</c>): the credential's own person, or <c>credentials.manage</c>
/// (<see cref="CredentialOwnerOrPermission.Permits"/>, which the pipeline applies) — and the person must still stand in the team, which the
/// service holds while it writes.</item>
/// <item>Reconnect a sign-in (<c>POST /api/credentials/oauth/init</c> naming the credential): the same bar, and one more because it signs in AS
/// the person — the reach over them (<see cref="CredentialOwnerOrPermission.PermitsReconnect"/>, which the service applies and the OAuth
/// callback asks again). An Admin reconnects a Member's sign-in, never another Admin's or an Owner's; their own person always may.</item>
/// <item><c>runs.launch</c> (Member and up): run it again.</item>
/// </list>
/// </summary>
public sealed record CallerStanding(bool MayManageRepositories, bool MayReplaceTheToken, bool MayReconnectTheCredential, bool MayLaunchRuns)
{
    /// <summary>
    /// <paramref name="person"/> is who the credential belongs to (<see cref="CredentialPerson.Of"/>), and null when it belongs to no one — a team's
    /// own: its token is replaced by whoever holds <c>credentials.manage</c>, and it has no sign-in to reconnect.
    /// </summary>
    public static CallerStanding Of(TeamRole role, CredentialPerson? person)
    {
        var owns = person is { IsCaller: true };
        var personStands = person is null || person.Standing is not null;

        return new(
            MayManageRepositories: TeamPermissionMatrix.IsGranted(role, TeamPermissions.ReposManage),
            MayReplaceTheToken: personStands && CredentialOwnerOrPermission.Permits(role, owns, TeamPermissions.CredentialsManage),
            MayReconnectTheCredential: CredentialOwnerOrPermission.PermitsReconnect(role, owns, person?.Standing),
            MayLaunchRuns: TeamPermissionMatrix.IsGranted(role, TeamPermissions.RunsLaunch));
    }

    /// <summary>The fix for a credential of this kind: a pasted token is replaced, a sign-in is reconnected — the one pick <see cref="RemedyActions"/> makes.</summary>
    public bool MayRepair(bool staticToken) => staticToken ? MayReplaceTheToken : MayReconnectTheCredential;
}

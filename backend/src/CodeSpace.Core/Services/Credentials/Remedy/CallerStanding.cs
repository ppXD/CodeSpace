using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Services.Credentials.Remedy;

/// <summary>
/// What the person asking may do about a provider failure, decided by the SAME rules the endpoints that do it apply — so a button
/// is enabled exactly when the request behind it would be accepted. Pure: the team role and whether the credential is the
/// caller's own are the only inputs.
///
/// <list type="bullet">
/// <item><c>repos.manage</c> (Admin and Owner): re-link a repository, edit a provider's address.</item>
/// <item><c>credentials.manage</c> (Admin and Owner): reconnect a sign-in. <c>POST /api/credentials/oauth/init</c> is a
/// <c>credentials.manage</c> request whoever the credential belongs to — owning the row does not pass it.</item>
/// <item>The credential's own person, or <c>credentials.manage</c>: replace a pasted token — the rule
/// <see cref="Core.Authorization.CredentialOwnerOrPermission"/> applies to <c>PUT /api/credentials/{id}/token</c>.</item>
/// <item><c>runs.launch</c> (Member and up): run it again.</item>
/// </list>
/// </summary>
public sealed record CallerStanding(bool MayManageRepositories, bool MayReconnectTheCredential, bool MayReplaceTheToken, bool MayLaunchRuns)
{
    /// <summary><paramref name="ownsTheCredential"/>: the credential is Personal and the caller is its person — which passes the credential rule on membership alone.</summary>
    public static CallerStanding Of(TeamRole role, bool ownsTheCredential) => new(
        MayManageRepositories: TeamPermissionMatrix.IsGranted(role, TeamPermissions.ReposManage),
        MayReconnectTheCredential: TeamPermissionMatrix.IsGranted(role, TeamPermissions.CredentialsManage),
        MayReplaceTheToken: ownsTheCredential || TeamPermissionMatrix.IsGranted(role, TeamPermissions.CredentialsManage),
        MayLaunchRuns: TeamPermissionMatrix.IsGranted(role, TeamPermissions.RunsLaunch));
}

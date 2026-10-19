using CodeSpace.Core.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// The one definition of "the credential's own person, or someone holding the permission" — what the credential pipeline tiers
/// enforce, what the provider remedy's <c>CallerStanding</c> decides its buttons by, and what the OAuth callback asks again of the
/// person who started a reconnect. Pure, so the whole table is pinned here; the pipeline's and the callback's use of it is pinned
/// against real rows in the integration tier.
///
/// <para>A reconnect adds the rank reach: it acts on a PERSON as well as on a credential, so holding the permission is not enough to
/// reconnect the sign-in of someone of one's own rank or above — the guard every act on a member keeps.</para>
/// </summary>
[Trait("Category", "Unit")]
public class CredentialOwnerOrPermissionTests
{
    [Theory]
    // role, owns the Personal credential, permission, permitted
    [InlineData(TeamRole.Owner, false, TeamPermissions.CredentialsManage, true)]
    [InlineData(TeamRole.Admin, false, TeamPermissions.CredentialsManage, true)]
    [InlineData(TeamRole.Member, false, TeamPermissions.CredentialsManage, false)]
    [InlineData(TeamRole.Viewer, false, TeamPermissions.CredentialsManage, false)]
    [InlineData(TeamRole.Owner, true, TeamPermissions.CredentialsManage, true)]
    [InlineData(TeamRole.Admin, true, TeamPermissions.CredentialsManage, true)]
    [InlineData(TeamRole.Member, true, TeamPermissions.CredentialsManage, true)]
    [InlineData(TeamRole.Viewer, true, TeamPermissions.CredentialsManage, true)]
    [InlineData(TeamRole.Member, false, TeamPermissions.ReposManage, false)]
    [InlineData(TeamRole.Member, true, TeamPermissions.ReposManage, true)]
    [InlineData(TeamRole.Admin, false, TeamPermissions.ReposManage, true)]
    [InlineData(TeamRole.Viewer, true, TeamPermissions.ReposManage, true)]
    public void A_credentials_own_person_passes_on_membership_and_anyone_else_needs_the_permission(TeamRole role, bool owns, string permission, bool permitted)
    {
        CredentialOwnerOrPermission.Permits(role, owns, permission).ShouldBe(permitted);
    }

    [Theory]
    // role of whoever reconnects, whether the sign-in is theirs, the role its person stands as in the team, permitted
    [InlineData(TeamRole.Owner, false, TeamRole.Admin, true)]
    [InlineData(TeamRole.Owner, false, TeamRole.Member, true)]
    [InlineData(TeamRole.Owner, false, TeamRole.Viewer, true)]
    [InlineData(TeamRole.Owner, false, TeamRole.Owner, false)]    // never across, and nobody ranks above an Owner
    [InlineData(TeamRole.Admin, false, TeamRole.Member, true)]
    [InlineData(TeamRole.Admin, false, TeamRole.Viewer, true)]
    [InlineData(TeamRole.Admin, false, TeamRole.Admin, false)]    // two Admins who could reconnect each other's sign-ins turn a disagreement into a race
    [InlineData(TeamRole.Admin, false, TeamRole.Owner, false)]
    [InlineData(TeamRole.Member, false, TeamRole.Viewer, false)]  // credentials.manage is the permission, and a Member lacks it whoever the person is
    [InlineData(TeamRole.Viewer, false, TeamRole.Viewer, false)]
    [InlineData(TeamRole.Owner, true, TeamRole.Owner, true)]      // yourself is always in reach of yourself
    [InlineData(TeamRole.Admin, true, TeamRole.Admin, true)]
    [InlineData(TeamRole.Member, true, TeamRole.Member, true)]
    [InlineData(TeamRole.Viewer, true, TeamRole.Viewer, true)]
    public void A_reconnect_needs_the_permission_or_the_credential_and_reach_over_the_person(TeamRole role, bool owns, TeamRole person, bool permitted)
    {
        CredentialOwnerOrPermission.PermitsReconnect(role, owns, person).ShouldBe(permitted);
    }

    [Theory]
    [InlineData(TeamRole.Owner, false)]
    [InlineData(TeamRole.Admin, false)]
    [InlineData(TeamRole.Member, true)]
    [InlineData(TeamRole.Viewer, true)]
    public void A_sign_in_whose_person_has_no_standing_in_the_team_is_reconnected_by_nobody(TeamRole role, bool owns)
    {
        // Someone who left has no one to sign in as, and a team's own credential has no person at all: the service refuses both, so no role does.
        CredentialOwnerOrPermission.PermitsReconnect(role, owns, ownerStanding: null).ShouldBeFalse();
    }
}

using CodeSpace.Core.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Auth;

/// <summary>
/// The disclosure boundary of a 403, pinned on both sides.
///
/// <para>Two failure modes, opposite in kind and both real. Saying too much confirms a team, a
/// repository or a credential to someone who only guessed an id — the reason the client message was
/// masked in the first place. Saying too little left a Member who pressed an Admin-only button with
/// "Access denied for this tenant." and no way to discover that a role change was the entire fix;
/// that sentence also shipped the word "tenant", which names nothing an operator has ever seen.</para>
///
/// <para>So each case is pinned for what it MUST say and for what it must NOT: the tests below assert
/// the actionable half is present AND that no identifier crosses the boundary.</para>
/// </summary>
[Trait("Category", "Unit")]
public class TenantAccessDeniedMessageTests
{
    private static readonly Guid User = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Team = Guid.Parse("22222222-2222-2222-2222-222222222222");

    /// <summary>The case that sent the owner hunting: the fix is a role change and the old message never said so.</summary>
    [Fact]
    public void A_role_refusal_names_the_role_held_and_the_role_needed()
    {
        var denial = TenantAccessDeniedException.MissingTeamPermission(User, Team, TeamRole.Member, TeamPermissions.ReposManage);

        denial.ClientMessage.ShouldNotBeNull();
        denial.ClientMessage!.ShouldContain("Member", Case.Sensitive, "the caller's own role is a fact about the caller");
        denial.ClientMessage.ShouldContain("Admin", Case.Sensitive, "and the role that would work is what makes it actionable");
        denial.Details.ShouldNotBeNull();
        denial.Details!["yourRole"].ShouldBe("Member");
        denial.Details["requiredRole"].ShouldBe("Admin");
        denial.Details["requiredPermission"].ShouldBe(TeamPermissions.ReposManage);
    }

    /// <summary>
    /// The required role is read from the matrix, not restated at the call site — so a permission that
    /// is re-tiered later cannot leave this sentence telling operators the old answer.
    /// </summary>
    [Theory]
    [InlineData(TeamPermissions.WorkflowsWrite, "Member")]
    [InlineData(TeamPermissions.ReposManage, "Admin")]
    [InlineData(TeamPermissions.TeamManage, "Owner")]
    public void The_role_a_refusal_asks_for_is_the_one_the_matrix_requires(string permission, string expected)
    {
        TenantAccessDeniedException.MissingTeamPermission(User, Team, TeamRole.Viewer, permission)
            .Details!["requiredRole"].ShouldBe(expected);
    }

    [Fact]
    public void A_missing_team_selection_says_so_rather_than_reading_as_a_permission_problem()
    {
        var denial = TenantAccessDeniedException.NoTeamSelected(User, "X-Team-Id");

        denial.ClientMessage.ShouldContain("No team is selected");
        denial.ClientMessage.ShouldNotContain("X-Team-Id", Case.Sensitive, "a header name is ours, not something an operator can act on");
    }

    [Fact]
    public void An_unauthenticated_caller_is_told_to_sign_in()
    {
        TenantAccessDeniedException.NotAuthenticated(Team).ClientMessage.ShouldContain("not signed in");
    }

    [Fact]
    public void An_instance_administrator_action_says_it_is_one()
    {
        TenantAccessDeniedException.GlobalAdminRequired(User, Roles.Admin).ClientMessage.ShouldContain("administrator");
    }

    /// <summary>
    /// Everything that could confirm the existence of a team, a repository or a credential keeps the
    /// plain constructor, and the plain constructor says nothing. This is the half that must not drift:
    /// a future factory added for convenience here would leak by default.
    /// </summary>
    [Fact]
    public void A_refusal_about_something_the_caller_cannot_see_says_nothing_about_it()
    {
        var denial = new TenantAccessDeniedException(User, Team, $"repository {Team} not found or not accessible");

        denial.ClientMessage.ShouldBe("You don't have access to this.");
        denial.Details.ShouldBeNull();
    }

    /// <summary>
    /// No identifier, on any path. The diagnostic message carries both ids for the log; repeating one
    /// back would confirm an id to someone who guessed it, which is the whole reason this is masked.
    /// </summary>
    [Fact]
    public void No_refusal_ever_hands_back_a_user_or_team_id()
    {
        IFailure[] denials = [
            TenantAccessDeniedException.MissingTeamPermission(User, Team, TeamRole.Member, TeamPermissions.ReposManage),
            TenantAccessDeniedException.NoTeamSelected(User, "X-Team-Id"),
            TenantAccessDeniedException.NotAuthenticated(Team),
            TenantAccessDeniedException.MissingGlobalPermission(User, "users.manage"),
            TenantAccessDeniedException.GlobalAdminRequired(User, Roles.Admin),
            new TenantAccessDeniedException(User, Team, "user is not a member of this team"),
        ];

        foreach (var denial in denials)
        {
            var wire = denial.ClientMessage + " " + string.Join(" ", denial.Details?.Values ?? []);
            wire.ShouldNotContain(User.ToString(), Case.Insensitive);
            wire.ShouldNotContain(Team.ToString(), Case.Insensitive);
        }
    }

    /// <summary>The diagnostic message keeps both ids — masking the client must not have masked the log.</summary>
    [Fact]
    public void The_log_still_names_the_user_and_the_team()
    {
        var denial = TenantAccessDeniedException.MissingTeamPermission(User, Team, TeamRole.Member, TeamPermissions.ReposManage);

        denial.Message.ShouldContain(User.ToString());
        denial.Message.ShouldContain(Team.ToString());
        denial.Message.ShouldContain(TeamPermissions.ReposManage);
    }
}

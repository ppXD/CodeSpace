using System;
using System.Linq;
using System.Reflection;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Pins the team authorization table: the rank ordering, every permission's wire string, and which
/// role each permission starts at.
///
/// <para>Why pin it: the rank/role mapping IS the product's access policy, and it is the kind of
/// thing a refactor silently widens — flipping one entry from Admin to Member hands every member the
/// ability to mint billable model credentials, and nothing else in the build would notice.</para>
/// </summary>
[Trait("Category", "Unit")]
public class TeamPermissionMatrixTests
{
    [Fact]
    public void The_rank_ladder_is_pinned()
    {
        // TeamRole's own member order is REVERSED (Owner=0 … Viewer=3). Anything that compares the
        // enum directly inverts every check, so the ladder lives here and is asserted explicitly.
        TeamRoleRank.Of(TeamRole.Owner).ShouldBe(40);
        TeamRoleRank.Of(TeamRole.Admin).ShouldBe(30);
        TeamRoleRank.Of(TeamRole.Member).ShouldBe(20);
        TeamRoleRank.Of(TeamRole.Viewer).ShouldBe(10);

        TeamRoleRank.Of(TeamRole.Owner).ShouldBeGreaterThan(TeamRoleRank.Of(TeamRole.Admin));
        TeamRoleRank.Of(TeamRole.Admin).ShouldBeGreaterThan(TeamRoleRank.Of(TeamRole.Member));
        TeamRoleRank.Of(TeamRole.Member).ShouldBeGreaterThan(TeamRoleRank.Of(TeamRole.Viewer));
    }

    [Fact]
    public void Every_TeamRole_has_a_rank()
    {
        // A new role that reaches authorization unranked must fail loudly, not sort below Viewer.
        foreach (var role in Enum.GetValues<TeamRole>()) TeamRoleRank.Of(role).ShouldBeGreaterThan(0);
    }

    [Fact]
    public void The_permission_values_are_pinned()
    {
        // Every reference today is through the constants, so this catches nothing a compiler would
        // miss. It is here for the moment that stops being true: the first time a permission is
        // projected to a client or written against an account, the VALUE becomes the contract, and
        // whatever a rename produced that day silently becomes it instead.
        TeamPermissions.WorkflowsWrite.ShouldBe("workflows.write");
        TeamPermissions.RunsLaunch.ShouldBe("runs.launch");
        TeamPermissions.RunsControl.ShouldBe("runs.control");
        TeamPermissions.RunsDecide.ShouldBe("runs.decide");
        TeamPermissions.SessionsWrite.ShouldBe("sessions.write");
        TeamPermissions.AgentsWrite.ShouldBe("agents.write");
        TeamPermissions.VariablesWrite.ShouldBe("variables.write");
        TeamPermissions.ChatWrite.ShouldBe("chat.write");
        TeamPermissions.CredentialsUse.ShouldBe("credentials.use");
        TeamPermissions.ReposManage.ShouldBe("repos.manage");
        TeamPermissions.CredentialsManage.ShouldBe("credentials.manage");
        TeamPermissions.StorageManage.ShouldBe("storage.manage");
        TeamPermissions.ModelsManage.ShouldBe("models.manage");
        TeamPermissions.MembersManage.ShouldBe("members.manage");
        TeamPermissions.TeamManage.ShouldBe("team.manage");
    }

    [Theory]
    [InlineData(TeamPermissions.WorkflowsWrite, TeamRole.Member)]
    [InlineData(TeamPermissions.RunsLaunch, TeamRole.Member)]
    [InlineData(TeamPermissions.RunsControl, TeamRole.Member)]
    [InlineData(TeamPermissions.RunsDecide, TeamRole.Member)]
    [InlineData(TeamPermissions.SessionsWrite, TeamRole.Member)]
    [InlineData(TeamPermissions.AgentsWrite, TeamRole.Member)]
    [InlineData(TeamPermissions.VariablesWrite, TeamRole.Member)]
    [InlineData(TeamPermissions.ChatWrite, TeamRole.Member)]
    [InlineData(TeamPermissions.CredentialsUse, TeamRole.Member)]
    [InlineData(TeamPermissions.ReposManage, TeamRole.Admin)]
    [InlineData(TeamPermissions.CredentialsManage, TeamRole.Admin)]
    [InlineData(TeamPermissions.StorageManage, TeamRole.Admin)]
    [InlineData(TeamPermissions.ModelsManage, TeamRole.Admin)]
    [InlineData(TeamPermissions.MembersManage, TeamRole.Admin)]
    [InlineData(TeamPermissions.TeamManage, TeamRole.Owner)]
    public void The_minimum_role_per_permission_is_pinned(string permission, TeamRole expected) =>
        TeamPermissionMatrix.MinimumRoleFor(permission).ShouldBe(expected);

    [Fact]
    public void Viewer_holds_nothing()
    {
        // The whole point of Viewer: membership grants reads, and no permission at all grants writes.
        foreach (var permission in TeamPermissionMatrix.All) TeamPermissionMatrix.IsGranted(TeamRole.Viewer, permission).ShouldBeFalse($"Viewer must not hold '{permission}' — Viewer is the read-only role");
    }

    [Fact]
    public void Owner_holds_everything()
    {
        foreach (var permission in TeamPermissionMatrix.All) TeamPermissionMatrix.IsGranted(TeamRole.Owner, permission).ShouldBeTrue($"Owner must hold '{permission}'");
    }

    [Theory]
    [InlineData(TeamRole.Member, TeamPermissions.WorkflowsWrite, true)]
    [InlineData(TeamRole.Member, TeamPermissions.ModelsManage, false)]
    [InlineData(TeamRole.Member, TeamPermissions.TeamManage, false)]
    [InlineData(TeamRole.Admin, TeamPermissions.WorkflowsWrite, true)]
    [InlineData(TeamRole.Admin, TeamPermissions.ModelsManage, true)]
    [InlineData(TeamRole.Admin, TeamPermissions.TeamManage, false)]
    [InlineData(TeamRole.Owner, TeamPermissions.TeamManage, true)]
    public void A_role_holds_every_permission_at_or_below_its_rank(TeamRole role, string permission, bool granted) =>
        TeamPermissionMatrix.IsGranted(role, permission).ShouldBe(granted);

    [Fact]
    public void The_matrix_covers_every_declared_permission()
    {
        // A constant with no matrix row would throw at request time — in the one code path that
        // needed it, in production. Catch it here instead.
        var uncovered = DeclaredPermissions().Where(p => !TeamPermissionMatrix.All.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        uncovered.ShouldBeEmpty("these TeamPermissions constants have no row in TeamPermissionMatrix, so any request declaring one throws at runtime:\n  " + string.Join("\n  ", uncovered));
    }

    [Fact]
    public void The_matrix_has_no_row_that_is_not_a_declared_permission()
    {
        // The reverse direction: a row keyed by a literal nobody can reference is dead policy.
        var declared = DeclaredPermissions().ToHashSet(StringComparer.Ordinal);
        var orphans = TeamPermissionMatrix.All.Where(p => !declared.Contains(p)).OrderBy(p => p, StringComparer.Ordinal).ToList();

        orphans.ShouldBeEmpty("these TeamPermissionMatrix rows are not declared as TeamPermissions constants:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void Unknown_permissions_are_rejected_rather_than_defaulted() =>
        Should.Throw<ArgumentOutOfRangeException>(() => TeamPermissionMatrix.IsGranted(TeamRole.Owner, "runs.lanuch"));

    private static string[] DeclaredPermissions()
    {
        var constants = typeof(TeamPermissions).GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToArray();

        constants.ShouldNotBeEmpty("the reflection scan found no permission constants — the checks below would pass vacuously");

        return constants;
    }
}

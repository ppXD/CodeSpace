using System;
using System.Collections.Generic;
using System.Linq;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Mediation;
using Shouldly;

namespace CodeSpace.UnitTests.Architecture;

/// <summary>
/// Every team-scoped write declares a permission, or is named below as one that mutates only the
/// caller's own state.
///
/// <para>Why the exemption list is explicit rather than implied: "membership is enough here" is a
/// judgement about blast radius, and the difference between a read cursor and a team credential is
/// invisible in a diff. Naming each one with its reason makes adding a 4th an argument someone has
/// to make, instead of a marker someone forgot.</para>
/// </summary>
[Trait("Category", "Unit")]
public class TeamWritePermissionAdoptionTests
{
    /// <summary>
    /// Writes that touch only the row belonging to the caller, or act only as the caller's own
    /// identity. A Viewer performing one of these changes nothing they could not change without
    /// CodeSpace, so gating them would deny people their own state.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SelfServiceByDesign = new Dictionary<string, string>
    {
        ["MarkConversationReadCommand"] = "advances the caller's own read cursor; unread counts are per-member and the cursor only moves forward.",
        ["LinkProviderIdentityByPatCommand"] = "links the caller's own GitHub/GitLab account; the service scopes by ICurrentUser.Id, never by team.",
        ["UnlinkProviderIdentityCommand"] = "unlinks the caller's own provider identity, same scoping.",
        ["LeaveTeamCommand"] = "removes the caller's own membership row; a Viewer who cannot leave a team has been locked in.",
        ["SubmitPullRequestReviewCommand"] = "posts a review only AS the caller's own linked provider identity — the handler always supplies the caller as the actor, so the provider's permission for that person is the gate, and they could post the same review from the provider's own page.",
    };

    [Fact]
    public void Every_team_write_declares_a_permission_or_is_self_service()
    {
        var ungated = TeamWriteCommands()
            .Where(t => !typeof(IRequireTeamPermission).IsAssignableFrom(t))
            .Where(t => !typeof(IRequireCredentialManagement).IsAssignableFrom(t))
            .Where(t => !SelfServiceByDesign.ContainsKey(t.Name))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        ungated.ShouldBeEmpty(
            "these team-scoped writes are gated by membership alone, so any Viewer can perform them. Declare the " +
            "permission from TeamPermissions that fits — or, if it mutates only the caller's own state, add it to " +
            "SelfServiceByDesign with the reason:\n  " + string.Join("\n  ", ungated));
    }

    [Theory]
    [InlineData("UnbindRepositoryCommand", TeamPermissions.ReposManage)]
    [InlineData("RelinkRepositoryCredentialCommand", TeamPermissions.ReposManage)]
    [InlineData("TestRepositoryBindingCommand", TeamPermissions.ReposManage)]
    public void A_write_addressed_by_a_resource_id_holds_the_bar_of_the_write_that_creates_it(string command, string permission)
    {
        // These carried only their resource marker, which proves team and membership and no role — so a Viewer
        // could revoke the Owner's credential, re-point or unbind a repository, and spend its credential on a probe.
        var type = TeamWriteCommands().SingleOrDefault(t => t.Name == command);

        type.ShouldNotBeNull($"{command} is no longer seen as a team write — the scan below must keep covering resource-keyed commands");
        typeof(IRequireTeamPermission).IsAssignableFrom(type!).ShouldBeTrue($"{command} must declare a team permission, not rely on its resource marker alone");
        Probe(type!).RequiredPermission.ShouldBe(permission);
    }

    [Fact]
    public void Revoking_a_credential_takes_credentials_manage_unless_it_is_the_callers_own_personal_one()
    {
        // The credential marker alone let a Viewer disconnect the Owner's credential. A plain CredentialsManage gate
        // closed that and also stopped every member disconnecting the personal token they had linked themselves — so
        // the gate is the management marker, whose behaviour lets only the owner of a Personal credential through.
        var type = TeamWriteCommands().SingleOrDefault(t => t.Name == "RevokeCredentialCommand");

        type.ShouldNotBeNull("RevokeCredentialCommand is no longer seen as a team write — the scan below must keep covering credential-keyed commands");
        typeof(IRequireCredentialManagement).IsAssignableFrom(type!).ShouldBeTrue("revoking a credential must be gated by IRequireCredentialManagement, not by its resource marker alone");
    }

    [Fact]
    public void The_self_service_list_does_not_rot()
    {
        foreach (var name in SelfServiceByDesign.Keys)
        {
            var type = TeamWriteCommands().SingleOrDefault(t => t.Name == name);

            type.ShouldNotBeNull($"self-service write '{name}' no longer exists — remove it from SelfServiceByDesign");
            typeof(IRequireTeamPermission).IsAssignableFrom(type!).ShouldBeFalse($"self-service write '{name}' now declares a permission — remove it from SelfServiceByDesign");
        }
    }

    [Fact]
    public void Every_permission_in_the_matrix_gates_at_least_one_write()
    {
        // A row nothing declares is policy that cannot be violated, which reads as enforcement and is
        // not. members.manage and team.manage are the honest exceptions: the commands they will gate
        // do not exist yet, and inventing them to satisfy a test would be worse than naming the gap.
        var unenforceable = new[] { TeamPermissions.MembersManage, TeamPermissions.TeamManage };

        var declared = TeamWriteCommands()
            .Where(t => typeof(IRequireTeamPermission).IsAssignableFrom(t))
            .Select(t => Probe(t).RequiredPermission)
            .ToHashSet(StringComparer.Ordinal);

        var orphans = TeamPermissionMatrix.All
            .Where(p => !declared.Contains(p) && !unenforceable.Contains(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        orphans.ShouldBeEmpty("these permissions gate nothing, so the matrix promises access control it does not apply:\n  " + string.Join("\n  ", orphans));
    }

    [Fact]
    public void No_write_is_gated_by_a_permission_the_matrix_does_not_know()
    {
        var unknown = TeamWriteCommands()
            .Where(t => typeof(IRequireTeamPermission).IsAssignableFrom(t))
            .Select(t => new { t.Name, Permission = Probe(t).RequiredPermission })
            .Where(x => !TeamPermissionMatrix.All.Contains(x.Permission))
            .Select(x => $"{x.Name} → '{x.Permission}'")
            .ToList();

        unknown.ShouldBeEmpty("these writes declare a permission with no matrix row, so every call throws at runtime:\n  " + string.Join("\n  ", unknown));
    }

    /// <summary>
    /// RequiredPermission returns a constant and touches no state, so an uninitialized instance can be
    /// asked for it — these records have required members and cannot otherwise be constructed here.
    /// </summary>
    private static IRequireTeamPermission Probe(Type type) =>
        (IRequireTeamPermission)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);

    /// <summary>
    /// Every command scoped to a team: by the team header, or by a team-owned resource it names. The
    /// resource markers are part of the scan because they do not extend <see cref="IRequireTeamMembership"/>
    /// — they check the resource's team and the caller's membership, and no role — so a write carrying only
    /// one of them used to be invisible here while being exactly the write this test exists to catch.
    /// </summary>
    private static IEnumerable<Type> TeamWriteCommands()
    {
        var types = typeof(TeamPermissions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IRequireTeamMembership).IsAssignableFrom(t) || typeof(IRequireRepositoryAccess).IsAssignableFrom(t) || typeof(IRequireCredentialAccess).IsAssignableFrom(t))
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
            .ToList();

        types.ShouldNotBeEmpty("the reflection scan found no team writes — every check in this class would pass vacuously");

        return types;
    }
}

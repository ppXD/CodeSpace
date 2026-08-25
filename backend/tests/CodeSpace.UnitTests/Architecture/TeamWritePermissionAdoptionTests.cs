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
    /// Writes that touch only the row belonging to the caller. A Viewer performing one of these
    /// changes nothing another member can observe, so gating them would deny people their own state.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> SelfServiceByDesign = new Dictionary<string, string>
    {
        ["MarkConversationReadCommand"] = "advances the caller's own read cursor; unread counts are per-member and the cursor only moves forward.",
        ["LinkProviderIdentityByPatCommand"] = "links the caller's own GitHub/GitLab account; the service scopes by ICurrentUser.Id, never by team.",
        ["UnlinkProviderIdentityCommand"] = "unlinks the caller's own provider identity, same scoping.",
        ["LeaveTeamCommand"] = "removes the caller's own membership row; a Viewer who cannot leave a team has been locked in.",
    };

    [Fact]
    public void Every_team_write_declares_a_permission_or_is_self_service()
    {
        var ungated = TeamWriteCommands()
            .Where(t => !typeof(IRequireTeamPermission).IsAssignableFrom(t))
            .Where(t => !SelfServiceByDesign.ContainsKey(t.Name))
            .Select(t => t.FullName!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        ungated.ShouldBeEmpty(
            "these team-scoped writes are gated by membership alone, so any Viewer can perform them. Declare the " +
            "permission from TeamPermissions that fits — or, if it mutates only the caller's own state, add it to " +
            "SelfServiceByDesign with the reason:\n  " + string.Join("\n  ", ungated));
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

    private static IEnumerable<Type> TeamWriteCommands()
    {
        var types = typeof(TeamPermissions).Assembly.GetTypes()
            .Where(t => t.IsClass && !t.IsAbstract)
            .Where(t => typeof(IRequireTeamMembership).IsAssignableFrom(t))
            .Where(t => t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(ICommand<>)))
            .ToList();

        types.ShouldNotBeEmpty("the reflection scan found no team writes — every check in this class would pass vacuously");

        return types;
    }
}

using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Authorization;

/// <summary>
/// The single authority on which <see cref="TeamRole"/> holds which team permission.
///
/// <para>Committed code with a pinned test rather than <c>role_permission</c> rows: the matrix is a
/// product decision that ships in a reviewable diff. A per-instance DB override would let two
/// deployments enforce different rules with nothing in the source to say so, and there is no
/// operator need to re-cut the tiers per install — the instance-level <c>permission</c> tables stay
/// reserved for grants that genuinely vary per account.</para>
///
/// <para>READS ARE NOT IN THIS TABLE. Team membership alone is read access; only writes and actions
/// declare a permission. That keeps adoption a write-side-only change — no query is touched.</para>
///
/// <para>Adoption is INCOMPLETE. Most team writes still pass on membership alone, so Viewer is not yet
/// a read-only role in practice; <c>TeamWritePermissionAdoptionTests</c> names every write that still
/// is, and is the honest count of what this table does not yet govern.</para>
/// </summary>
public static class TeamPermissionMatrix
{
    private static readonly Dictionary<string, TeamRole> MinimumRole = new(StringComparer.Ordinal)
    {
        [TeamPermissions.WorkflowsWrite] = TeamRole.Member,
        [TeamPermissions.RunsLaunch] = TeamRole.Member,
        [TeamPermissions.RunsControl] = TeamRole.Member,
        [TeamPermissions.RunsDecide] = TeamRole.Member,
        [TeamPermissions.SessionsWrite] = TeamRole.Member,
        [TeamPermissions.AgentsWrite] = TeamRole.Member,
        [TeamPermissions.VariablesWrite] = TeamRole.Member,
        [TeamPermissions.ChatWrite] = TeamRole.Member,
        [TeamPermissions.CredentialsUse] = TeamRole.Member,
        [TeamPermissions.ReposManage] = TeamRole.Admin,
        [TeamPermissions.CredentialsManage] = TeamRole.Admin,
        [TeamPermissions.ModelsManage] = TeamRole.Admin,
        [TeamPermissions.MembersManage] = TeamRole.Admin,
        [TeamPermissions.TeamManage] = TeamRole.Owner
    };

    /// <summary>Every permission the matrix knows. Used by the convention test that proves it covers every declared constant.</summary>
    public static IReadOnlyCollection<string> All => MinimumRole.Keys;

    public static TeamRole MinimumRoleFor(string permission) =>
        MinimumRole.TryGetValue(permission, out var role) ? role : throw new ArgumentOutOfRangeException(nameof(permission), permission, "unknown team permission — declare it in TeamPermissions and map it here");

    public static bool IsGranted(TeamRole role, string permission) => TeamRoleRank.Of(role) >= TeamRoleRank.Of(MinimumRoleFor(permission));

    /// <summary>
    /// Everything a role holds, for handing to a client so it can hide what it must not offer.
    ///
    /// <para>Sorted so the list is a stable value: it is embedded in a cached /me response, and an
    /// order that varies per call would make two identical answers look like a change.</para>
    /// </summary>
    public static IReadOnlyList<string> GrantedTo(TeamRole role) =>
        MinimumRole.Where(entry => TeamRoleRank.Of(role) >= TeamRoleRank.Of(entry.Value)).Select(entry => entry.Key).OrderBy(code => code, StringComparer.Ordinal).ToArray();
}

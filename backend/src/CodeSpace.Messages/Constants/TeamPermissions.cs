namespace CodeSpace.Messages.Constants;

/// <summary>
/// Team-scoped capability names. A request declares one via <c>IRequireTeamPermission</c> and
/// <c>TeamPermissionMatrix</c> maps it to the minimum <c>TeamRole</c> that holds it.
///
/// <para>Distinct from <see cref="Permissions"/>: those are INSTANCE-level grants stored in
/// <c>permission</c> / <c>user_permission</c> and answer "may this account do X anywhere". These are
/// TENANT-level and answer "what may this account do inside the team on X-Team-Id" — derived from
/// the caller's <c>team_membership.role</c>, never stored per user.</para>
///
/// <para>Deliberately one file, not a partial class split per domain: the whole point of the table is
/// that a reviewer can read every capability the product has in one screen.</para>
///
/// <para>Nothing outside this assembly reads these as strings yet — every reference goes through the
/// constants, so a rename is still a compiler-checked refactor. The VALUES are pinned by a unit test
/// anyway, because an authorization decision is keyed on the value the moment one is projected to a
/// client or stored against an account, and that contract is cheaper to settle now than to discover
/// after the first deployment has persisted it.</para>
/// </summary>
public static class TeamPermissions
{
    public const string WorkflowsWrite = "workflows.write";
    public const string RunsLaunch = "runs.launch";
    public const string RunsControl = "runs.control";
    public const string RunsDecide = "runs.decide";
    public const string SessionsWrite = "sessions.write";
    public const string AgentsWrite = "agents.write";
    public const string VariablesWrite = "variables.write";
    public const string ChatWrite = "chat.write";

    /// <summary>Spend a bound credential to act on the provider as the team (open a PR, post a review).</summary>
    public const string CredentialsUse = "credentials.use";

    public const string ReposManage = "repos.manage";
    public const string CredentialsManage = "credentials.manage";
    public const string ModelsManage = "models.manage";
    public const string MembersManage = "members.manage";
    public const string TeamManage = "team.manage";
}

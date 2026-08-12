namespace CodeSpace.Messages.Constants;

/// <summary>
/// INSTANCE-level capability names — grants that belong to an account across the whole deployment,
/// stored in the <c>permission</c> / <c>role_permission</c> / <c>user_permission</c> tables.
///
/// <para>Not to be confused with <see cref="TeamPermissions"/>, which is the far larger set and
/// answers "what may this account do inside the team on X-Team-Id". That one lives in committed code
/// with a pinned test, because it is the product's access policy and belongs in a reviewable diff.
/// This one lives in DATA, because it is the rare grant that varies per account on a given
/// deployment — the difference is whether an operator needs to change it for one person without
/// shipping a build.</para>
///
/// <para>Kept deliberately small. A capability that can be expressed as a team role belongs there.</para>
/// </summary>
public static partial class Permissions
{
    /// <summary>
    /// May open a new workspace. Instance-level because creating a team is not an action inside any
    /// team — there is no team to hold the role that would grant it.
    /// </summary>
    public const string TeamsCreate = "teams.create";
}

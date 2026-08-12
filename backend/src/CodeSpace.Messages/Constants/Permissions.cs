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

    /// <summary>
    /// What every account holds from the moment it exists.
    ///
    /// <para>Opening a workspace is something anyone here may do: you become its owner and can invite
    /// people into it, which is how the product is meant to be used rather than a privilege to hand
    /// out. It stays a GRANT rather than becoming "any signed-in caller" so that it remains
    /// revocable — taking it back from one account is deleting a row, not shipping a build.</para>
    ///
    /// <para>Adding to this list needs a backfill migration for the accounts that already exist;
    /// granting on creation only reaches the ones made afterwards. <c>DefaultAccountPermissionsTests</c>
    /// pins the list so that is a visible decision.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> GrantedToEveryAccount = [TeamsCreate];
}

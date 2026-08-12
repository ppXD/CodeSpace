namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — operation requires a named INSTANCE-level capability, held by an account across the whole
/// deployment rather than inside one team.
///
/// <para>The sibling of <c>IRequireTeamPermission</c>, and the distinction is not a technicality:
/// team permissions answer "what may you do in the team on X-Team-Id", and some things have no team
/// to be in. Creating one is the obvious case — there is no team yet to hold the role that would
/// grant it.</para>
///
/// <para>Reach for a team permission first. This tier exists for grants an operator must be able to
/// hand to one person without shipping a build, and every capability that fits a role does not.</para>
/// </summary>
public interface IRequireGlobalPermission : IRequireAuthenticatedUser
{
    /// <summary>A <c>Permissions</c> constant.</summary>
    string RequiredGlobalPermission { get; }
}

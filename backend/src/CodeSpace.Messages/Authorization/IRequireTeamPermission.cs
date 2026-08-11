namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — a team-scoped WRITE or ACTION that needs a named capability on top of bare membership.
///
/// <para>Extends <see cref="IRequireTeamMembership"/> deliberately: the existing membership behavior
/// still resolves and vets the <c>X-Team-Id</c> team, and this tier only adds the role check inside
/// it. One marker on the request buys both, and a request can never end up permission-checked but
/// tenancy-unchecked.</para>
///
/// <para>Queries do NOT carry this marker. Membership is read access — see
/// <c>TeamPermissionMatrix</c>.</para>
/// </summary>
public interface IRequireTeamPermission : IRequireTeamMembership
{
    /// <summary>A <c>TeamPermissions</c> constant. Never a literal — the constants are the wire contract.</summary>
    string RequiredPermission { get; }
}

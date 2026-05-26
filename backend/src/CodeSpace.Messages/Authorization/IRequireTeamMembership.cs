namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — operation is scoped to the team identified by the X-Team-Id request header.
/// The pipeline behavior reads ICurrentTeam and verifies the caller is a member (Admin
/// role bypasses). Messages do NOT carry TeamId in their body — that's why this interface
/// is a pure marker with no property.
/// </summary>
public interface IRequireTeamMembership
{
}

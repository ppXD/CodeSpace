namespace CodeSpace.Messages.Authorization;

/// <summary>
/// Marker — operation requires any authenticated user but no team scope (e.g. ListMyTeams,
/// GetMyProfile, CreateTeam). Pipeline behavior just asserts ICurrentUser.Id is non-null.
/// </summary>
public interface IRequireAuthenticatedUser
{
}

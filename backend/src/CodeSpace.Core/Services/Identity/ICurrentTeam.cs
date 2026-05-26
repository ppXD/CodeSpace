namespace CodeSpace.Core.Services.Identity;

/// <summary>
/// The team the current request is scoped to — sourced from the X-Team-Id header. Every
/// team-scoped command/query (IRequireTeamMembership) reads its team from here instead of
/// carrying TeamId in the payload. Endpoints that are not team-scoped (login, /me, admin)
/// ignore this; <see cref="IsSet"/> is false in that case.
/// </summary>
public interface ICurrentTeam
{
    Guid? Id { get; }
    bool IsSet { get; }
}

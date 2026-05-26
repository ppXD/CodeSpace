namespace CodeSpace.Core.Authorization;

/// <summary>
/// Thrown by every tenant-authorization pipeline behavior when the calling user is not a
/// member of the team the request targets. Mapped to HTTP 403 by GlobalExceptionFilter.
/// Carries structured fields so logs / metrics can pivot on (UserId, TeamId, Reason).
/// </summary>
public sealed class TenantAccessDeniedException : Exception
{
    public TenantAccessDeniedException(Guid? userId, Guid teamId, string reason)
        : base($"User {userId?.ToString() ?? "<anonymous>"} is not authorized for team {teamId}: {reason}")
    {
        UserId = userId;
        TeamId = teamId;
        Reason = reason;
    }

    public Guid? UserId { get; }
    public Guid TeamId { get; }
    public string Reason { get; }
}

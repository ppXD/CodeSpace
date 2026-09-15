using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Authorization;

/// <summary>
/// Thrown by every tenant-authorization pipeline behavior when the calling user is not allowed to act
/// on the team the request targets. Mapped to HTTP 403 by GlobalExceptionFilter. Carries structured
/// fields so logs / metrics can pivot on (UserId, TeamId, Reason).
///
/// <para><b>What the caller is told, and why it varies.</b> The diagnostic message names a user and a
/// team; repeating that back would confirm a team id to someone who guessed it, so it never reaches
/// the client. But masking every refusal identically left the one thing an operator could act on —
/// their OWN role, and the role the action needs — as unreachable as the things they must not learn.
/// A Member pressing an Admin-only button read "Access denied for this tenant." and had no way to
/// discover that a role change was the whole fix.</para>
///
/// <para>So the disclosure rule is drawn here, once, in named factories: a refusal may tell the caller
/// about THEMSELVES — their role, the role required, whether they are signed in, whether a team is
/// selected — and must stay silent about anything that would confirm the existence, membership, or
/// contents of something they cannot already see. The plain constructor is the silent default; a
/// factory exists only where there is something safe to say.</para>
/// </summary>
public sealed class TenantAccessDeniedException : Exception, IFailure
{
    /// <summary>Says nothing about what was asked for. Every refusal that could confirm a team, a repository or a credential lands here.</summary>
    private const string Opaque = "You don't have access to this.";

    public FailureKind Kind => FailureKind.Forbidden;

    public string Code => FailureCodes.Forbidden;

    public string? ClientMessage { get; }

    public IReadOnlyDictionary<string, object?>? Details { get; }

    public TenantAccessDeniedException(Guid? userId, Guid teamId, string reason) : this(userId, teamId, reason, Opaque, null) { }

    private TenantAccessDeniedException(Guid? userId, Guid teamId, string reason, string clientMessage, IReadOnlyDictionary<string, object?>? details)
        : base($"User {userId?.ToString() ?? "<anonymous>"} is not authorized for team {teamId}: {reason}")
    {
        UserId = userId;
        TeamId = teamId;
        Reason = reason;
        ClientMessage = clientMessage;
        Details = details;
    }

    public Guid? UserId { get; }
    public Guid TeamId { get; }
    public string Reason { get; }

    /// <summary>No request can name a team, so nothing about any team is being confirmed by saying so.</summary>
    public static TenantAccessDeniedException NoTeamSelected(Guid? userId, string headerName) =>
        new(userId, Guid.Empty, $"{headerName} header missing", "No team is selected. Pick a team, then try again.", null);

    /// <summary>Whether the CALLER is signed in is a fact about the caller.</summary>
    public static TenantAccessDeniedException NotAuthenticated(Guid teamId) =>
        new(null, teamId, "no authenticated user on request", "You're not signed in.", null);

    /// <summary>
    /// The one refusal a person can act on alone. Both roles are facts about the caller's own standing
    /// — <c>/me</c> already carries their role and everything it grants — and the required role is a
    /// shipped product decision, not tenant data.
    /// </summary>
    public static TenantAccessDeniedException MissingTeamPermission(Guid? userId, Guid teamId, TeamRole role, string permission)
    {
        var required = TeamPermissionMatrix.MinimumRoleFor(permission);

        return new TenantAccessDeniedException(
            userId, teamId, $"role '{role}' does not hold permission '{permission}'",
            $"Your role on this team is {role}, but this needs {required} or higher. Ask someone with that role to do it, or to change yours.",
            new Dictionary<string, object?> { ["yourRole"] = role.ToString(), ["requiredRole"] = required.ToString(), ["requiredPermission"] = permission });
    }

    /// <summary>An instance-level grant the caller either holds or does not — no team is named.</summary>
    public static TenantAccessDeniedException MissingGlobalPermission(Guid? userId, string permission) =>
        new(userId, Guid.Empty, $"permission '{permission}' required",
            $"This needs the '{permission}' permission on this instance, which your account doesn't hold.",
            new Dictionary<string, object?> { ["requiredPermission"] = permission });

    public static TenantAccessDeniedException GlobalAdminRequired(Guid? userId, string role) =>
        new(userId, Guid.Empty, $"role '{role}' required",
            "This is an instance-administrator action, and your account isn't an administrator.",
            new Dictionary<string, object?> { ["requiredRole"] = role });
}

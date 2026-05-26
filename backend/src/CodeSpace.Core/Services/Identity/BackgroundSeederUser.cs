using CodeSpace.Messages.Constants;

namespace CodeSpace.Core.Services.Identity;

/// <summary>
/// The principal used when there is no HTTP context (Hangfire workers, scheduled jobs,
/// DbUp migrations). Mirrors the DB-side seeded system user from migration 0004 —
/// same UUID, same Admin role. Hardcoding the [Admin] role here avoids a DB roundtrip
/// on every background DI scope; the DB row exists for audit consistency.
/// </summary>
public sealed class BackgroundSeederUser : ICurrentUser
{
    private static readonly IReadOnlyList<string> SeedRoles = new[] { CodeSpace.Messages.Constants.Roles.Admin };

    public Guid? Id => SystemUsers.SeederId;

    public string Name => SystemUsers.SeederName;

    public IReadOnlyList<string> Roles => SeedRoles;

    /// <summary>Empty — Admin role short-circuits any HasPermission check at the behavior layer.</summary>
    public IReadOnlyList<string> Permissions { get; } = Array.Empty<string>();

    public bool HasRole(string role) => SeedRoles.Contains(role, StringComparer.OrdinalIgnoreCase);

    public bool HasPermission(string permission) => HasRole(CodeSpace.Messages.Constants.Roles.Admin);

    /// <summary>Background flows have no password to rotate — always false.</summary>
    public bool PasswordMustChange => false;
}

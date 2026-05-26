namespace CodeSpace.Core.Services.Identity;

/// <summary>
/// The caller of the current operation. Lifted from JWT claims on the HTTP path, or set to
/// the seeded system user on the background path (Hangfire workers, scheduled jobs, DbUp).
/// Authorization behaviors decide what's allowed based on <see cref="HasRole"/> /
/// <see cref="HasPermission"/>.
/// </summary>
public interface ICurrentUser
{
    Guid? Id { get; }

    string Name { get; }

    /// <summary>Role machine names this user holds (via role_user joined with role.status=true).</summary>
    IReadOnlyList<string> Roles { get; }

    /// <summary>Union of permissions granted by Roles AND directly via user_permission.</summary>
    IReadOnlyList<string> Permissions { get; }

    bool HasRole(string role);

    bool HasPermission(string permission);

    /// <summary>
    /// True when the user is signed in but the API must reject everything except
    /// ChangePasswordCommand until they rotate their password (see migration 0007).
    /// Backed by a per-scope cached DB lookup so repeated reads in a single request are
    /// free.
    /// </summary>
    bool PasswordMustChange { get; }
}

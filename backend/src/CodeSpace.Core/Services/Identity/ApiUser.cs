using System.Security.Claims;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Identity;

public sealed class ApiUser : ICurrentUser, IScopedDependency
{
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly Func<CodeSpaceDbContext> _dbFactory;

    // Scope-level cache. Roles + Permissions are joined from DB on first access and
    // re-used for the rest of the request — no per-handler refetch.
    private IReadOnlyList<string>? _cachedRoles;
    private IReadOnlyList<string>? _cachedPermissions;
    private bool? _cachedPasswordMustChange;

    // DbContext is taken as a Func<> to break the ICurrentUser ↔ CodeSpaceDbContext
    // construction cycle. The DbContext factory in CodeSpaceModule asks for ICurrentUser
    // so its audit pipeline can stamp CreatedBy / LastModifiedBy; if ApiUser took the
    // DbContext directly, both sides would block each other during resolution. Deferring
    // via Func<> means ApiUser is fully constructed (and ICurrentUser cached in the
    // lifetime scope) before any DB call happens — at which point DbContext resolves
    // cleanly and sees the cached ICurrentUser.
    public ApiUser(IHttpContextAccessor httpContextAccessor, Func<CodeSpaceDbContext> dbFactory)
    {
        _httpContextAccessor = httpContextAccessor;
        _dbFactory = dbFactory;
    }

    private CodeSpaceDbContext Db => _dbFactory();

    public Guid? Id
    {
        get
        {
            var idClaim = ReadClaim(ClaimTypes.NameIdentifier);
            return Guid.TryParse(idClaim, out var id) ? id : null;
        }
    }

    public string Name => ReadClaim(ClaimTypes.Name) ?? string.Empty;

    public IReadOnlyList<string> Roles
    {
        get
        {
            if (_cachedRoles != null) return _cachedRoles;
            if (Id == null) return _cachedRoles = Array.Empty<string>();

            _cachedRoles = Db.RoleUser.AsNoTracking()
                .Where(ru => ru.UserId == Id.Value)
                .Join(Db.Role.AsNoTracking().Where(r => r.Status), ru => ru.RoleId, r => r.Id, (_, r) => r.Name)
                .ToList();

            return _cachedRoles;
        }
    }

    public IReadOnlyList<string> Permissions
    {
        get
        {
            if (_cachedPermissions != null) return _cachedPermissions;
            if (Id == null) return _cachedPermissions = Array.Empty<string>();

            var permissionsFromRoles = Db.RoleUser.AsNoTracking()
                .Where(ru => ru.UserId == Id.Value)
                .Join(Db.Role.AsNoTracking().Where(r => r.Status), ru => ru.RoleId, r => r.Id, (ru, _) => ru.RoleId)
                .Join(Db.RolePermission.AsNoTracking(), rid => rid, rp => rp.RoleId, (_, rp) => rp.PermissionId);

            var directPermissions = Db.UserPermission.AsNoTracking()
                .Where(up => up.UserId == Id.Value)
                .Select(up => up.PermissionId);

            _cachedPermissions = Db.Permission.AsNoTracking()
                .Where(p => permissionsFromRoles.Contains(p.Id) || directPermissions.Contains(p.Id))
                .Select(p => p.Name)
                .ToList();

            return _cachedPermissions;
        }
    }

    public bool HasRole(string role) => Roles.Contains(role, StringComparer.OrdinalIgnoreCase);

    public bool HasPermission(string permission) => Permissions.Contains(permission, StringComparer.OrdinalIgnoreCase);

    public bool PasswordMustChange
    {
        get
        {
            if (_cachedPasswordMustChange != null) return _cachedPasswordMustChange.Value;
            if (Id == null) return (_cachedPasswordMustChange = false).Value;

            _cachedPasswordMustChange = Db.User.AsNoTracking()
                .Where(u => u.Id == Id.Value && u.DeletedDate == null)
                .Select(u => u.PasswordMustChange)
                .FirstOrDefault();

            return _cachedPasswordMustChange.Value;
        }
    }

    private string? ReadClaim(string claimType)
    {
        var ctx = _httpContextAccessor.HttpContext;
        if (ctx == null) return null;

        var authScheme = ctx.User.Identity?.AuthenticationType;
        return ctx.User.Claims.FirstOrDefault(c => c.Type == claimType && c.Subject?.AuthenticationType == authScheme)?.Value;
    }
}

using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Messages.Authorization;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Commands.Auth;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Users;

public sealed class UserService : IUserService, IScopedDependency
{
    public const int MinPasswordLength = 12;

    private readonly CodeSpaceDbContext _db;
    private readonly ICurrentUser _currentUser;
    private readonly IPasswordHasher _hasher;
    private readonly IJwtTokenIssuer _tokenIssuer;

    public UserService(CodeSpaceDbContext db, ICurrentUser currentUser, IPasswordHasher hasher, IJwtTokenIssuer tokenIssuer)
    {
        _db = db;
        _currentUser = currentUser;
        _hasher = hasher;
        _tokenIssuer = tokenIssuer;
    }

    public async Task<SignInResponse> SignInAsync(string nameOrEmail, string password, CancellationToken cancellationToken)
    {
        var user = await LookupUserAsync(nameOrEmail, cancellationToken).ConfigureAwait(false);

        if (user == null || string.IsNullOrEmpty(user.PasswordHash) || !_hasher.Verify(password, user.PasswordHash))
            throw new InvalidCredentialsException();

        // Checked AFTER the password, so a wrong guess against a deactivated address still answers
        // "invalid credentials" — otherwise this endpoint becomes an oracle for which addresses exist.
        if (user.DeactivatedAt != null) throw new AccountDeactivatedException();

        user.LastLoginDate = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var issued = _tokenIssuer.Issue(user);
        var me = await BuildMeResponseAsync(user, cancellationToken).ConfigureAwait(false);

        return new SignInResponse { Token = issued.Token, ExpiresAt = issued.ExpiresAt, User = me };
    }

    public async Task<ChangePasswordResponse> ChangePasswordAsync(string currentPassword, string newPassword, CancellationToken cancellationToken)
    {
        var userId = _currentUser.Id ?? throw new UnauthorizedAccessException("authentication required");

        var user = await _db.User.FirstOrDefaultAsync(u => u.Id == userId && u.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException("user not found");

        EnsureNewPasswordValid(newPassword, currentPassword);
        EnsureCurrentPasswordMatches(user, currentPassword);

        user.PasswordHash = _hasher.Hash(newPassword);
        user.PasswordMustChange = false;
        // Ends every OTHER session for this account. Changing a password is what someone does when
        // they think a session is not theirs, and leaving those alive makes the act cosmetic.
        user.SecurityStamp = Guid.NewGuid();

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var me = await BuildMeResponseAsync(user, cancellationToken).ConfigureAwait(false);
        // Re-issued under the new stamp, so the caller who just rotated is not signed out by their own
        // change — every other token minted before it is.
        var reissued = _tokenIssuer.Issue(user);

        return new ChangePasswordResponse { User = me, Token = reissued.Token, ExpiresAt = reissued.ExpiresAt };
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken cancellationToken)
    {
        var userId = _currentUser.Id ?? throw new UnauthorizedAccessException("authentication required");

        var user = await _db.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"user {userId} not found");

        return await BuildMeResponseAsync(user, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Members of a team = its membership rows, owner included. It used to be the owner UNIONed with
    /// them, to cover teams whose owner was named on <c>team.owner_user_id</c> and nowhere else; that
    /// column is gone, so the union has nothing left to add and the roster is one table again.
    /// Soft-deleted users fall out; name-sorted for a stable picker.
    /// </summary>
    public async Task<IReadOnlyList<TeamMemberSummary>> ListTeamMembersAsync(Guid teamId, CancellationToken cancellationToken)
    {
        // Filtered through the team so a soft-deleted one lists nobody — membership rows are a
        // hard-delete junction table and survive it.
        var memberships = await _db.TeamMembership.AsNoTracking()
            .Where(m => m.TeamId == teamId && m.Team.DeletedDate == null)
            .Select(m => new { m.UserId, m.Role, m.CreatedDate })
            .ToDictionaryAsync(m => m.UserId, cancellationToken).ConfigureAwait(false);

        if (memberships.Count == 0) return Array.Empty<TeamMemberSummary>();

        var userIds = memberships.Keys.ToList();

        // The bot holds no role at all — it is not a person.
        var users = await _db.User.AsNoTracking()
            .Where(u => userIds.Contains(u.Id) && u.DeletedDate == null)
            .OrderBy(u => u.Name)
            .Select(u => new { u.Id, u.Name, u.Email, u.AvatarUrl, u.IsBot })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return users.Select(u => new TeamMemberSummary
        {
            UserId = u.Id,
            Name = u.Name,
            Email = u.Email,
            AvatarUrl = u.AvatarUrl,
            IsBot = u.IsBot,
            Role = u.IsBot ? null : memberships[u.Id].Role,
            JoinedAt = memberships[u.Id].CreatedDate,
        }).ToList();
    }

    /// <summary>
    /// Accept either an email or a display name. Both sides lowered so we don't need CITEXT —
    /// fine for small user tables; OrderBy(Id) makes collision-resolution deterministic. The
    /// wrong-password check above still throws InvalidCredentials, so a colliding name can't
    /// be used to enumerate a real user.
    /// </summary>
    private async Task<User?> LookupUserAsync(string identifier, CancellationToken cancellationToken)
    {
        var normalized = identifier.Trim().ToLowerInvariant();
        return await _db.User
            .Where(u => u.DeletedDate == null && (u.Email.ToLower() == normalized || u.Name.ToLower() == normalized))
            .OrderBy(u => u.Id)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void EnsureNewPasswordValid(string newPassword, string currentPassword)
    {
        if (string.IsNullOrEmpty(newPassword)) throw new InvalidOperationException("New password must not be empty.");
        if (newPassword.Length < MinPasswordLength) throw new InvalidOperationException($"New password must be at least {MinPasswordLength} characters.");
        if (newPassword == currentPassword) throw new InvalidOperationException("New password must differ from the current password.");
    }

    private void EnsureCurrentPasswordMatches(User user, string currentPassword)
    {
        if (string.IsNullOrEmpty(user.PasswordHash) || !_hasher.Verify(currentPassword, user.PasswordHash))
            throw new InvalidCredentialsException();
    }

    /// <summary>
    /// Shared MeResponse projection — used by sign-in, change-password, and the /me query so
    /// all three return the identical shape. Personal team first then Workspaces by name,
    /// matching the sidebar's grouping so the SPA doesn't have to re-sort.
    /// </summary>
    /// <summary>
    /// The account's OWN instance grants, read for the account being described rather than taken from
    /// <c>ICurrentUser</c>.
    ///
    /// <para>Two of the three callers here are anonymous at the moment they run — sign-in and
    /// invitation acceptance both build this response for someone who had no session when the request
    /// started. Reading the ambient principal returned an empty list for exactly those, and the client
    /// caches this response as its answer to "what may I do", so the grant stayed invisible until
    /// something else refetched.</para>
    /// </summary>
    private async Task<IReadOnlyList<string>> LoadInstancePermissionsAsync(Guid userId, CancellationToken cancellationToken)
    {
        var fromRoles = _db.RoleUser.AsNoTracking()
            .Where(ru => ru.UserId == userId)
            .Join(_db.Role.AsNoTracking().Where(r => r.Status), ru => ru.RoleId, r => r.Id, (ru, _) => ru.RoleId)
            .Join(_db.RolePermission.AsNoTracking(), rid => rid, rp => rp.RoleId, (_, rp) => rp.PermissionId);

        var granted = _db.UserPermission.AsNoTracking().Where(up => up.UserId == userId).Select(up => up.PermissionId);

        return await _db.Permission.AsNoTracking()
            .Where(p => fromRoles.Contains(p.Id) || granted.Contains(p.Id))
            .Select(p => p.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<MeResponse> BuildMeForAsync(User user, CancellationToken cancellationToken) => await BuildMeResponseAsync(user, cancellationToken).ConfigureAwait(false);

    private async Task<MeResponse> BuildMeResponseAsync(User user, CancellationToken cancellationToken)
    {
        var teams = await _db.Team.AsNoTracking()
            .Where(t => t.DeletedDate == null && t.Memberships.Any(m => m.UserId == user.Id))
            .Select(t => new
            {
                t.Id,
                t.Slug,
                t.Name,
                t.Kind,
                // Nullable only because the projection cannot see that the Where above guarantees a
                // row; TeamRole.Owner is the zero value, so a plain FirstOrDefault() would hand Owner
                // to anyone a future predicate change let through.
                MembershipRole = t.Memberships.Where(m => m.UserId == user.Id).Select(m => (TeamRole?)m.Role).FirstOrDefault(),
                // People, counted the same way the roster lists them: membership rows minus departed
                // accounts.
                MemberCount = _db.User.Count(u => u.DeletedDate == null && t.Memberships.Any(m => m.UserId == u.Id)),
                RepositoryCount = _db.Repository.Count(r => r.TeamId == t.Id && r.DeletedDate == null),
                // Sidebar "Projects" row badge — counts active projects only. The "default" project is
                // seeded by the first repository bind (RepositoryBindingService), not by listing, so a
                // team that has never bound a repo reads 0 here.
                ProjectCount = _db.Project.Count(p => p.TeamId == t.Id && p.DeletedDate == null),
                // Sidebar "Workflows" row badge — active workflows for the team (same filter as the list query).
                WorkflowCount = _db.Workflow.Count(w => w.TeamId == t.Id && w.DeletedDate == null)
            })
            .OrderBy(t => t.Kind == TeamKind.Personal ? 0 : 1)
            .ThenBy(t => t.Name)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return new MeResponse
        {
            Id = user.Id,
            Email = user.Email,
            Name = user.Name,
            AvatarUrl = user.AvatarUrl,
            PasswordMustChange = user.PasswordMustChange,
            Permissions = await LoadInstancePermissionsAsync(user.Id, cancellationToken).ConfigureAwait(false),
            Teams = teams.Select(t => new MeTeam
            {
                Id = t.Id,
                Slug = t.Slug,
                Name = t.Name,
                Kind = t.Kind,
                Role = t.MembershipRole!.Value,
                Permissions = TeamPermissionMatrix.GrantedTo(t.MembershipRole!.Value),
                MemberCount = t.MemberCount,
                RepositoryCount = t.RepositoryCount,
                ProjectCount = t.ProjectCount,
                WorkflowCount = t.WorkflowCount
            }).ToList()
        };
    }
}

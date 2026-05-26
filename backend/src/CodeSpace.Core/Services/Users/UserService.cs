using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
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

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var me = await BuildMeResponseAsync(user, cancellationToken).ConfigureAwait(false);
        return new ChangePasswordResponse { User = me };
    }

    public async Task<MeResponse> GetMeAsync(CancellationToken cancellationToken)
    {
        var userId = _currentUser.Id ?? throw new UnauthorizedAccessException("authentication required");

        var user = await _db.User.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId && u.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"user {userId} not found");

        return await BuildMeResponseAsync(user, cancellationToken).ConfigureAwait(false);
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
    private async Task<MeResponse> BuildMeResponseAsync(User user, CancellationToken cancellationToken)
    {
        var teams = await _db.Team.AsNoTracking()
            .Where(t => t.DeletedDate == null && (t.OwnerUserId == user.Id || t.Memberships.Any(m => m.UserId == user.Id)))
            .Select(t => new
            {
                t.Id,
                t.Slug,
                t.Name,
                t.Kind,
                t.OwnerUserId,
                MembershipRole = t.Memberships.Where(m => m.UserId == user.Id).Select(m => (TeamRole?)m.Role).FirstOrDefault(),
                MemberCount = t.Memberships.Count() + 1,    // memberships + owner
                RepositoryCount = _db.Repository.Count(r => r.TeamId == t.Id && r.DeletedDate == null)
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
            Teams = teams.Select(t => new MeTeam
            {
                Id = t.Id,
                Slug = t.Slug,
                Name = t.Name,
                Kind = t.Kind,
                Role = t.OwnerUserId == user.Id ? TeamRole.Owner : (t.MembershipRole ?? TeamRole.Member),
                MemberCount = t.MemberCount,
                RepositoryCount = t.RepositoryCount
            }).ToList()
        };
    }
}

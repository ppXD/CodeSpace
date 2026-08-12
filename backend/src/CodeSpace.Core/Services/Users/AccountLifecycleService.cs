using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Auth;
using CodeSpace.Core.Settings.Application;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Users;

/// <summary>
/// Switching an account off, back on, and giving someone a way back in when they are locked out.
///
/// <para>Every operation here that should end existing sessions rotates the security stamp, and that
/// is the whole revocation mechanism: no list to store, nothing to sweep, and the next request any
/// stale token makes is refused. Forgetting the rotation is the way to get this wrong, so each site
/// does it next to the change that motivated it.</para>
/// </summary>
public sealed class AccountLifecycleService : IAccountLifecycleService, IScopedDependency
{
    /// <summary>Short by design: a reset link is a temporary key to an account, and the window is the exposure.</summary>
    public static readonly TimeSpan ResetLifetime = TimeSpan.FromHours(1);

    private readonly CodeSpaceDbContext _db;
    private readonly IPasswordHasher _hasher;
    private readonly PublicBaseUrlSetting _baseUrl;
    private readonly TimeProvider _clock;

    public AccountLifecycleService(CodeSpaceDbContext db, IPasswordHasher hasher, PublicBaseUrlSetting baseUrl, TimeProvider clock)
    {
        _db = db;
        _hasher = hasher;
        _baseUrl = baseUrl;
        _clock = clock;
    }

    public async Task<IReadOnlyList<AccountSummary>> ListAsync(CancellationToken cancellationToken) =>
        await _db.User.AsNoTracking()
            .Where(u => u.DeletedDate == null && !u.IsBot)
            .OrderBy(u => u.Name)
            .Select(u => new AccountSummary
            {
                Id = u.Id,
                Name = u.Name,
                Email = u.Email,
                IsDeactivated = u.DeactivatedAt != null,
                PasswordMustChange = u.PasswordMustChange,
                LastLoginDate = u.LastLoginDate,
            })
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    /// <summary>
    /// Switches the account off and ends its sessions in the same write. Deactivating without rotating
    /// would leave whoever holds a live token up to a day of continued access — which is the exact
    /// situation someone reaches for this to stop.
    /// </summary>
    public async Task DeactivateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken).ConfigureAwait(false);

        user.DeactivatedAt = _clock.GetUtcNow();
        user.SecurityStamp = Guid.NewGuid();

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ReactivateAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken).ConfigureAwait(false);

        user.DeactivatedAt = null;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Issues a link that lets someone set a new password without knowing the old one. Returned once —
    /// only the digest is stored — and delivered by whoever asked for it, the same shape invitations
    /// use, because there is no mail infrastructure to lean on.
    /// </summary>
    public async Task<PasswordResetLink> IssueResetAsync(Guid userId, CancellationToken cancellationToken)
    {
        var user = await LoadAsync(userId, cancellationToken).ConfigureAwait(false);

        var token = MintToken();

        user.PasswordResetTokenHash = HashToken(token);
        user.PasswordResetExpiresAt = _clock.GetUtcNow() + ResetLifetime;

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        return new PasswordResetLink { ResetUrl = _baseUrl.PasswordResetUrl(token), ExpiresAt = user.PasswordResetExpiresAt.Value };
    }

    /// <summary>
    /// Spends a reset token. Anonymous: the holder has no session, which is the situation the link
    /// exists for.
    ///
    /// <para>Consuming the token, setting the password, clearing the rotation flag and rotating the
    /// stamp are one write. A reset that left old sessions alive would hand the account back while
    /// whoever prompted the reset still had it.</para>
    /// </summary>
    public async Task ResetPasswordAsync(string token, string newPassword, CancellationToken cancellationToken)
    {
        var hash = HashToken(token);

        var user = await _db.User.SingleOrDefaultAsync(u => u.PasswordResetTokenHash == hash && u.DeletedDate == null, cancellationToken).ConfigureAwait(false);

        if (user == null || user.PasswordResetExpiresAt == null || user.PasswordResetExpiresAt <= _clock.GetUtcNow()) throw new PasswordResetNotUsableException();

        // A deactivated account must not be recoverable by whoever holds a link issued before it was
        // switched off — otherwise deactivation is undone by a piece of paper.
        if (user.DeactivatedAt != null) throw new PasswordResetNotUsableException();

        if (newPassword.Length < UserService.MinPasswordLength) throw new ArgumentException($"Password must be at least {UserService.MinPasswordLength} characters.", nameof(newPassword));

        user.PasswordHash = _hasher.Hash(newPassword);
        user.PasswordMustChange = false;
        user.PasswordResetTokenHash = null;
        user.PasswordResetExpiresAt = null;
        user.SecurityStamp = Guid.NewGuid();

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<User> LoadAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.User.SingleOrDefaultAsync(u => u.Id == userId && u.DeletedDate == null, cancellationToken).ConfigureAwait(false)
            ?? throw new KeyNotFoundException($"user {userId} not found");

    private static string MintToken()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);

        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static string HashToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

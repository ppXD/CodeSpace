using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// Decides whether a structurally valid token still represents a live session.
///
/// <para>A JWT is a signed statement about the past: it says who signed in and when, and it keeps
/// saying it for its full 24 hours no matter what happens to the account afterwards. Before this,
/// changing a password left every token minted under the old one working, and there was no way to
/// switch an account off at all — the only revocation available was waiting.</para>
///
/// <para>Checked at token validation rather than in a pipeline behavior on purpose: that is the one
/// point every authenticated request passes through, including the streaming endpoints and anything
/// that does not go near the mediator. One read per request, on a table already read for roles.</para>
/// </summary>
public sealed class SessionValidator : ISessionValidator, IScopedDependency
{
    /// <summary>The claim carrying the stamp the token was minted under.</summary>
    public const string SecurityStampClaim = "sst";

    private readonly CodeSpaceDbContext _db;

    public SessionValidator(CodeSpaceDbContext db) { _db = db; }

    public async Task<SessionVerdict> VerifyAsync(Guid userId, string? presentedStamp, CancellationToken cancellationToken)
    {
        var account = await _db.User.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.DeletedDate, u.DeactivatedAt, u.SecurityStamp })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        if (account == null || account.DeletedDate != null) return SessionVerdict.UnknownAccount;

        if (account.DeactivatedAt != null) return SessionVerdict.Deactivated;

        // A token minted before the stamp column existed carries no claim. Treating that as valid
        // would leave a bypass open for exactly as long as the oldest such token lives; treating it
        // as stale costs those sessions one sign-in and closes it now.
        if (presentedStamp == null || !Guid.TryParse(presentedStamp, out var stamp) || stamp != account.SecurityStamp) return SessionVerdict.Superseded;

        return SessionVerdict.Live;
    }
}

/// <summary>
/// Why a token is being refused, kept distinct because they are different facts even though the
/// caller is told the same thing: one is an account that was switched off, one is a session that was
/// ended, and the log has to be able to tell them apart when someone asks why they were signed out.
/// </summary>
public enum SessionVerdict
{
    Live,
    UnknownAccount,
    Deactivated,
    Superseded,
}

public interface ISessionValidator
{
    Task<SessionVerdict> VerifyAsync(Guid userId, string? presentedStamp, CancellationToken cancellationToken);
}

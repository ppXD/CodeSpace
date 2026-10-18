using System.Security.Cryptography;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.OAuth;

public sealed class OAuthStateStore : IOAuthStateStore, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly TimeProvider _clock;

    public OAuthStateStore(CodeSpaceDbContext db, TimeProvider clock) { _db = db; _clock = clock; }

    public async Task<OAuthPendingState> CreateAsync(OAuthPendingStateInput input, CancellationToken cancellationToken)
    {
        var row = new OAuthPendingState
        {
            State = NewState(),
            ProviderInstanceId = input.ProviderInstanceId,
            TeamId = input.TeamId,
            InitiatorUserId = input.InitiatorUserId,
            CodeVerifier = input.CodeVerifier,
            IntendedDisplayName = input.IntendedDisplayName,
            IntendedOwnerUserId = input.IntendedOwnerUserId,
            ReturnUrl = input.ReturnUrl,
            ReplaceCredentialId = input.ReplaceCredentialId,
            BrowserNonceHash = input.BrowserNonceHash,
            RequestedScopes = input.RequestedScopes?.ToList(),
            ExpiresDate = _clock.GetUtcNow() + input.Ttl
        };

        await _db.OAuthPendingState.AddAsync(row, cancellationToken).ConfigureAwait(false);
        return row;
    }

    public async Task<OAuthPendingState?> ConsumeAsync(string state, string browserNonceHash, CancellationToken cancellationToken)
    {
        var row = await _db.OAuthPendingState.FirstOrDefaultAsync(r => r.State == state && r.BrowserNonceHash == browserNonceHash, cancellationToken).ConfigureAwait(false);

        return Spend(row);
    }

    public async Task<OAuthPendingState?> ConsumeForInitiatorAsync(string state, Guid initiatorUserId, CancellationToken cancellationToken)
    {
        var row = await _db.OAuthPendingState.FirstOrDefaultAsync(r => r.State == state && r.InitiatorUserId == initiatorUserId, cancellationToken).ConfigureAwait(false);

        return Spend(row);
    }

    public async Task<OAuthPendingState?> FindLiveAsync(string state, CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();

        return await _db.OAuthPendingState.AsNoTracking().FirstOrDefaultAsync(r => r.State == state && r.ExpiresDate > now, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Always delete on read — one-time-use enforcement. If expired we return null but
    /// still drop the row so it doesn't accumulate.
    /// </summary>
    private OAuthPendingState? Spend(OAuthPendingState? row)
    {
        if (row == null) return null;

        _db.OAuthPendingState.Remove(row);

        if (row.ExpiresDate < _clock.GetUtcNow()) return null;

        return row;
    }

    private static string NewState() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

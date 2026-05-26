using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.OAuth;

public sealed class OAuthStateCleanup : IOAuthStateCleanup, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly TimeProvider _clock;

    public OAuthStateCleanup(CodeSpaceDbContext db, TimeProvider clock) { _db = db; _clock = clock; }

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken)
    {
        var cutoff = _clock.GetUtcNow();
        return await _db.OAuthPendingState.Where(s => s.ExpiresDate < cutoff).ExecuteDeleteAsync(cancellationToken).ConfigureAwait(false);
    }
}

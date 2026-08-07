using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// Reads the unrotated-password roster and emits one Warning per user. Several pods running the same DB each log the
/// same warning — deliberate, so an operator sees the prompt in whichever log stream they happen to be looking at.
/// </summary>
public sealed class UnrotatedBootstrapPasswordAudit : IUnrotatedBootstrapPasswordAudit, IScopedDependency
{
    private readonly CodeSpaceDbContext _db;
    private readonly ILogger<UnrotatedBootstrapPasswordAudit> _logger;

    public UnrotatedBootstrapPasswordAudit(CodeSpaceDbContext db, ILogger<UnrotatedBootstrapPasswordAudit> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<int> WarnUnrotatedAsync(CancellationToken cancellationToken)
    {
        var pending = await LoadUnrotatedEmailsAsync(cancellationToken).ConfigureAwait(false);

        foreach (var email in pending) Warn(email);

        return pending.Count;
    }

    private async Task<List<string>> LoadUnrotatedEmailsAsync(CancellationToken cancellationToken) =>
        await _db.User.AsNoTracking()
            .Where(u => u.PasswordMustChange && u.DeletedDate == null)
            .Select(u => u.Email)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    private void Warn(string email) =>
        _logger.LogWarning(
            "Bootstrap password is unrotated for {Email}. Sign in and POST /api/auth/change-password to clear this warning. The default credentials are committed to source control — anyone with read access can sign in until rotation completes.",
            email);
}

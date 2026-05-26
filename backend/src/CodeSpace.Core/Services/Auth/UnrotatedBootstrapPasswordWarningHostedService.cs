using Autofac;
using CodeSpace.Core.Persistence.Db;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Auth;

/// <summary>
/// Logs a Warning on startup, and again every 30 minutes, for every user that still has
/// <c>password_must_change=true</c>. The bootstrap admin from migration 0006 will trip
/// this until the operator signs in and rotates. Multiple machines running the same DB
/// each log the same warning — that's deliberate, so operators see the prompt in any log
/// stream they happen to look at.
/// </summary>
public sealed class UnrotatedBootstrapPasswordWarningHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(30);

    private readonly ILifetimeScope _rootScope;
    private readonly ILogger<UnrotatedBootstrapPasswordWarningHostedService> _logger;

    public UnrotatedBootstrapPasswordWarningHostedService(ILifetimeScope rootScope, ILogger<UnrotatedBootstrapPasswordWarningHostedService> logger)
    {
        _rootScope = rootScope;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await CheckOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            await CheckOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task CheckOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _rootScope.BeginLifetimeScope();
            var db = scope.Resolve<CodeSpaceDbContext>();

            var pending = await db.User.AsNoTracking()
                .Where(u => u.PasswordMustChange && u.DeletedDate == null)
                .Select(u => u.Email)
                .ToListAsync(cancellationToken).ConfigureAwait(false);

            if (pending.Count == 0) return;

            foreach (var email in pending)
            {
                _logger.LogWarning(
                    "Bootstrap password is unrotated for {Email}. Sign in and POST /api/auth/change-password to clear this warning. The default credentials are committed to source control — anyone with read access can sign in until rotation completes.",
                    email);
            }
        }
        catch (Exception ex)
        {
            // Don't crash the host on DB transient errors; the next tick will retry.
            _logger.LogError(ex, "Unrotated-password check failed; will retry next tick");
        }
    }
}

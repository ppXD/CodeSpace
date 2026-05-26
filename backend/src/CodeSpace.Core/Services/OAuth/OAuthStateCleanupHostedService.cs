using Autofac;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.OAuth;

/// <summary>
/// Periodic janitor: every 5 minutes, sweep expired <c>oauth_pending_state</c> rows.
/// Backs off silently when nothing is due; logs at Information when it deletes anything.
/// Cancellation token shutdown is honored so the process exits cleanly.
/// </summary>
public sealed class OAuthStateCleanupHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ILifetimeScope _rootScope;
    private readonly ILogger<OAuthStateCleanupHostedService> _logger;

    public OAuthStateCleanupHostedService(ILifetimeScope rootScope, ILogger<OAuthStateCleanupHostedService> logger)
    {
        _rootScope = rootScope;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // First sweep on startup (the process may have been down for >TTL).
        await SweepOnceAsync(stoppingToken).ConfigureAwait(false);

        using var timer = new PeriodicTimer(Interval);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { return; }

            await SweepOnceAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private async Task SweepOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = _rootScope.BeginLifetimeScope();
            var cleaner = scope.Resolve<IOAuthStateCleanup>();

            var deleted = await cleaner.DeleteExpiredAsync(cancellationToken).ConfigureAwait(false);

            if (deleted > 0) _logger.LogInformation("Removed {Count} expired oauth_pending_state rows", deleted);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OAuth state cleanup sweep failed; will retry next tick");
        }
    }
}

namespace CodeSpace.Api.Extensions.Hangfire;

/// <summary>
/// Re-asserts this build's recurring-job set every <see cref="Interval"/> on a processing pod, healing an entry a
/// previous-version scheduler disabled.
///
/// <para>Every worker's Hangfire scheduler fires every recurring entry, its own or not. A previous-version worker cannot
/// load a job type it never had, retries the entry a few times, then disables it — no next execution — and nothing
/// re-enables it until a current worker boots again. In a rolling deploy the last current worker usually boots before
/// the last previous-version one stops, so the sweeps this build added could stay off indefinitely.
/// <c>AddOrUpdate</c> on an entry that carries a scheduling error reschedules it and resets its retries; a healthy
/// entry comes out unchanged.</para>
///
/// <para>Deliberately not an <c>IRecurringJob</c>: a previous-version scheduler would disable it exactly like the
/// entries it exists to heal. It runs only where the Worker registrar adds it, so never on a public pod, and it only
/// re-runs the registration that worker boot already performs — no business work.</para>
///
/// <para>A previous-version worker can still fetch a background job of a new type off the shared queue and fail it
/// (no retries); the next tick runs on a current worker. Nothing here can prevent that, and nothing is lost but the one
/// tick.</para>
/// </summary>
public sealed class RecurringJobScheduleKeeper : BackgroundService
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<RecurringJobScheduleKeeper> _logger;

    public RecurringJobScheduleKeeper(IServiceScopeFactory scopes, TimeProvider clock, ILogger<RecurringJobScheduleKeeper> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval, _clock);

        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) Reassert();
    }

    /// <summary>One pass, in its own scope. A failed pass is logged and left to the next one: a keeper that threw out of <see cref="ExecuteAsync"/> would stop the host.</summary>
    internal void Reassert()
    {
        try
        {
            using var scope = _scopes.CreateScope();
            RecurringJobSchedule.Assert(scope.ServiceProvider);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Re-asserting the recurring-job schedule failed; the next pass in {Interval} retries", Interval);
        }
    }
}

namespace CodeSpace.Core.Jobs;

/// <summary>
/// Recurring background job. Every implementation is a thin shell that dispatches a
/// Mediator command — the actual work lives in a handler where the rest of the pipeline
/// (UnitOfWork, Logging, Authorization) applies.
///
/// <para>API startup scans for all implementations, resolves each via DI, and registers
/// it with Hangfire's recurring-job scheduler through <see cref="IJobSafeRunner"/>.
/// Adding a new recurring job:</para>
/// <list type="number">
///   <item>Write a Mediator command.</item>
///   <item>Write its handler that owns the actual logic.</item>
///   <item>Write a <c>IRecurringJob</c> impl that's three lines — JobId, CronExpression,
///         and an <c>Execute</c> that mediator.Send's the command.</item>
/// </list>
/// </summary>
public interface IRecurringJob : IJob
{
    /// <summary>
    /// Cron expression. Hangfire's <c>Cron.*</c> helpers OR raw cron strings. Examples:
    /// <c>"*/5 * * * *"</c> (every 5 min), <c>"0 9 * * 1-5"</c> (weekdays 9am).
    /// </summary>
    string CronExpression { get; }

    /// <summary>UTC when null, which is the right default for server-side cron.</summary>
    TimeZoneInfo? TimeZone => null;
}

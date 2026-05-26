namespace CodeSpace.Core.Services.Jobs;

/// <summary>
/// Recurring background job descriptor. Every implementation is a THIN shell that dispatches
/// a Mediator command — the actual work lives in a <c>ICommandHandler&lt;T&gt;</c> (MediatR's
/// <c>IRequestHandler&lt;T&gt;</c>) where the rest of the pipeline (UnitOfWork, Logging,
/// Authorization) applies.
///
/// <para>The API project's startup scans for all implementations of this interface,
/// resolves each via DI, and registers it with Hangfire's <c>RecurringJob.AddOrUpdate</c>.
/// Adding a new recurring job means:
///   <list type="number">
///     <item>Write a Mediator command (<c>IRequest</c>).</item>
///     <item>Write its handler that owns the actual logic.</item>
///     <item>Write a <c>IRecurringJob</c> impl that's 3 lines — JobId, CronExpression,
///           and an <c>Execute</c> that mediator.Send's the command.</item>
///   </list>
/// Zero changes to startup wiring.</para>
/// </summary>
public interface IRecurringJob
{
    /// <summary>
    /// Stable id Hangfire indexes the recurring job by. <see cref="nameof"/> of the
    /// class is the standard convention so rename = new id + old one stays dead in Hangfire.
    /// </summary>
    string JobId { get; }

    /// <summary>
    /// Cron expression. Hangfire's <c>Cron.*</c> helpers OR raw cron strings. Examples:
    /// <c>"*/5 * * * *"</c> (every 5 min), <c>"0 9 * * 1-5"</c> (weekdays 9am).
    /// </summary>
    string CronExpression { get; }

    /// <summary>
    /// Body of the job. MUST be a thin mediator dispatch — no business logic here so handlers
    /// stay testable + reusable from non-recurring contexts.
    /// </summary>
    Task Execute();
}

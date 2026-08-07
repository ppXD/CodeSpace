using CodeSpace.Messages.Commands.Auth;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 30 minutes, dispatches <see cref="WarnUnrotatedBootstrapPasswordsCommand"/> so an operator running on the
/// committed bootstrap credentials keeps seeing the prompt to rotate. Thin Mediator dispatcher (Rule 14) — the
/// roster query and the warning live in <see cref="Services.Auth.IUnrotatedBootstrapPasswordAudit"/>.
///
/// <para>Replaces a <c>BackgroundService</c> that ran its own <c>PeriodicTimer</c> on EVERY pod; as a recurring job
/// it runs only where a Hangfire server does (the Worker role), so the warning is emitted once per interval per
/// worker rather than once per process.</para>
/// </summary>
public sealed class UnrotatedBootstrapPasswordWarningRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public UnrotatedBootstrapPasswordWarningRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(UnrotatedBootstrapPasswordWarningRecurringJob);
    public string CronExpression => "*/30 * * * *";   // every 30 minutes

    public async Task Execute() => await _mediator.Send(new WarnUnrotatedBootstrapPasswordsCommand()).ConfigureAwait(false);
}

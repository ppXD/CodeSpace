using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>
/// Every 5 minutes, dispatches <see cref="SweepCompletionShadowCommand"/> — terminal contract-era runs gain their
/// durable shadow assessment (P2a-4). Thin Mediator dispatcher (Rule 14); the compose chain lives in
/// <c>CompletionShadowService</c>. Shadow never mutates a terminal (Lock Clause 1) — a missed tick only delays a
/// RECORD, never a run.
/// </summary>
public sealed class CompletionShadowRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public CompletionShadowRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(CompletionShadowRecurringJob);
    public string CronExpression => "*/5 * * * *";

    public async Task Execute() => await _mediator.Send(new SweepCompletionShadowCommand()).ConfigureAwait(false);
}

using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Arc D / D1 — the nightly post-mortem: distill yesterday's failed/parked runs into the lesson ledger (thin Rule-14 dispatcher).</summary>
public sealed class LessonDistillationRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public LessonDistillationRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(LessonDistillationRecurringJob);
    public string CronExpression => "0 3 * * *";

    public async Task Execute() => await _mediator.Send(new DistillLessonsCommand()).ConfigureAwait(false);
}

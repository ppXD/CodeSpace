using CodeSpace.Messages.Commands.Agents;
using MediatR;

namespace CodeSpace.Core.Jobs.RecurringJobs;

/// <summary>Arc D — nightly lesson lifecycle: reconcile objective exposure outcomes, then distill fresh failures into experimental candidates.</summary>
public sealed class LessonDistillationRecurringJob : IRecurringJob
{
    private readonly IMediator _mediator;

    public LessonDistillationRecurringJob(IMediator mediator) { _mediator = mediator; }

    public string JobId => nameof(LessonDistillationRecurringJob);
    public string CronExpression => "0 3 * * *";

    public async Task Execute() => await _mediator.Send(new DistillLessonsCommand()).ConfigureAwait(false);
}

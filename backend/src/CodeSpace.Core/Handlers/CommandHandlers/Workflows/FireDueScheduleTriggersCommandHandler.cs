using CodeSpace.Core.Services.Workflows.RunSources.Schedule;
using CodeSpace.Messages.Commands.Workflows;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Workflows;

/// <summary>Rule 16 — thin handler. Reads the clock and hands off to <see cref="IScheduleTriggerService"/>,
/// which owns the due-detection + fire logic. The clock read lives here so the service stays a pure,
/// time-injected unit.</summary>
public sealed class FireDueScheduleTriggersCommandHandler : IRequestHandler<FireDueScheduleTriggersCommand, FireDueScheduleTriggersResponse>
{
    private readonly IScheduleTriggerService _schedules;

    public FireDueScheduleTriggersCommandHandler(IScheduleTriggerService schedules) { _schedules = schedules; }

    public async Task<FireDueScheduleTriggersResponse> Handle(FireDueScheduleTriggersCommand request, CancellationToken cancellationToken)
    {
        var fired = await _schedules.FireDueSchedulesAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);

        return new FireDueScheduleTriggersResponse { Fired = fired };
    }
}

using CodeSpace.Messages.Mediation;

namespace CodeSpace.Messages.Commands.Workflows;

/// <summary>
/// Fire every <c>trigger.schedule</c> activation whose cron is due. Dispatched by the recurring
/// schedule producer job each minute; can also be sent ad-hoc (admin endpoint / tests).
///
/// <para>NOT tenant-scoped — system-wide, runs without an actor context (the seeder identity).
/// Returns the count of runs created for log surfacing + the recurring-job result.</para>
/// </summary>
public sealed record FireDueScheduleTriggersCommand : ICommand<FireDueScheduleTriggersResponse>;

/// <summary>Count of scheduled runs the producer fired this tick (duplicates collapsed by the idempotency guard aren't counted).</summary>
public sealed record FireDueScheduleTriggersResponse
{
    public required int Fired { get; init; }
}

namespace CodeSpace.Core.Services.Workflows.RunSources.Schedule;

/// <summary>
/// Producer for the <c>trigger.schedule</c> trigger: finds every enabled schedule activation whose
/// cron is due in the look-back window ending at <paramref name="now"/> and fires a workflow run per
/// due occurrence. Idempotent — each (activation, scheduled-instant) pair fires at most once ever,
/// enforced by the run-request idempotency tuple — so overlapping or replayed ticks never double-fire.
/// </summary>
public interface IScheduleTriggerService
{
    /// <summary>Fire all schedule activations due in <c>(now - lookback, now]</c>. Returns the count of
    /// runs actually created (duplicates that the idempotency guard collapsed are not counted).</summary>
    Task<int> FireDueSchedulesAsync(DateTimeOffset now, CancellationToken cancellationToken);
}

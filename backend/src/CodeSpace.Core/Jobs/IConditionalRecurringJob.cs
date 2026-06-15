namespace CodeSpace.Core.Jobs;

/// <summary>
/// A recurring job that registers with the scheduler ONLY when <see cref="ShouldRegister"/> is true at startup. Lets a
/// feature-gated job (e.g. a reaper for a flag-OFF lane) stay completely un-scheduled — not merely a no-op tick — so a
/// flag-OFF deployment is byte-identical (no recurring entry, no fire). A plain <see cref="IRecurringJob"/> always
/// registers; only jobs that opt into conditional registration implement this.
/// </summary>
public interface IConditionalRecurringJob : IRecurringJob
{
    /// <summary>Evaluated once at startup during the recurring-job scan. False → the job is skipped (never scheduled).</summary>
    bool ShouldRegister { get; }
}

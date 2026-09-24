using CodeSpace.Core.Persistence;

namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// The bound on offering ONE database write that an agent's observer makes while the agent runs — a poll's event
/// batch, its spool offset — again after a TRANSIENT fault (<see cref="TransientDatabaseFault"/>).
///
/// <para>Why the observer retries at all. A poll's writes used to have no answer to a fault: the first connection reset
/// escaped the tail loop, the executor landed a healthy run Failed and deleted the workspace the still-running agent
/// was working in. A reset, a pool timeout, an admin shutdown or a failover blip is over in milliseconds to seconds and
/// says nothing about the agent's work, so the write is offered again, on the injected clock, until it lands or the
/// bound below is spent.</para>
///
/// <para>Why it is bounded twice. <see cref="MaxAttempts"/> alone does not bound how long the observer stops tailing,
/// because one attempt can itself hang for a connection or command timeout. <see cref="BudgetMilliseconds"/> is
/// measured from the first attempt, and no attempt starts once the next backoff would carry the write past it. What
/// happens past the bound is the caller's decision: the write gave up, it did not fail the run.</para>
///
/// <para>Committed here and changed by a pull request, like every other ceiling in this layer: a mistuned value costs
/// either lost events or a stalled tail on every running agent, and a review is the control that belongs in front of
/// it. Both constants are pinned by a test.</para>
/// </summary>
public static class ObserverWriteRetry
{
    /// <summary>Most attempts one write gets, the first included.</summary>
    public const int MaxAttempts = 8;

    /// <summary>The wall-clock budget, from the first attempt, past which no further attempt starts.</summary>
    public const int BudgetMilliseconds = 30_000;

    private const int FirstBackoffMilliseconds = 250;
    private const int MaxBackoffMilliseconds = 4_000;

    private static readonly TimeSpan Budget = TimeSpan.FromMilliseconds(BudgetMilliseconds);

    /// <summary>The wait after the <paramref name="failedAttempts"/>-th failed attempt: 250ms, doubled per attempt, capped at four seconds — a reset is asked about again almost at once, a longer blip every few seconds. Internal so a test pins the schedule and advances a virtual clock by exactly it.</summary>
    internal static TimeSpan BackoffAfter(int failedAttempts) => TimeSpan.FromMilliseconds(Math.Min(MaxBackoffMilliseconds, FirstBackoffMilliseconds << Math.Min(failedAttempts - 1, 5)));

    /// <summary>
    /// Offer <paramref name="write"/> until it lands or the bound is spent. Only a transient fault is offered again: any
    /// other exception — a verdict about the write, a lost fence — propagates from the attempt that raised it, exactly as
    /// it did before this retry existed, and a fault that arrives while the observer is being cancelled unwinds as that
    /// cancellation, because a torn-down worker's tear-down arm is who decides what happens next.
    /// </summary>
    public static async Task<Outcome> TryAsync(Func<CancellationToken, Task> write, TimeProvider clock, CancellationToken cancellationToken)
    {
        var startedAt = clock.GetUtcNow();
        var started = clock.GetTimestamp();

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await write(cancellationToken).ConfigureAwait(false);

                return new Outcome(attempt, startedAt, clock.GetElapsedTime(started), null);
            }
            catch (Exception fault) when (TransientDatabaseFault.Is(fault))
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (Spent(attempt, clock.GetElapsedTime(started))) return new Outcome(attempt, startedAt, clock.GetElapsedTime(started), fault);

                await Task.Delay(BackoffAfter(attempt), clock, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Whether the bound leaves no room for another attempt: the count is used up, or waiting for the next one would carry the write past the budget.</summary>
    private static bool Spent(int attempts, TimeSpan elapsed) => attempts >= MaxAttempts || elapsed + BackoffAfter(attempts) > Budget;

    /// <summary>How one write settled: how many attempts it took, when the first began, how long they all took, and — when it gave up — the last transient fault it saw.</summary>
    public readonly record struct Outcome(int Attempts, DateTimeOffset StartedAt, TimeSpan Elapsed, Exception? Fault)
    {
        public bool Landed => Fault is null;
    }
}

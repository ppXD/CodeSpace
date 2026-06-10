namespace CodeSpace.Core.Services.Agents;

/// <summary>
/// Pings a liveness heartbeat on a fixed cadence until cancelled. Pure + dependency-free — the ping is a
/// delegate — so the loop's behaviour (delay → ping, swallow a failed ping, stop on cancellation) is unit
/// tested without a database or a mocking framework. The executor wires the run's
/// <c>HeartbeatAsync</c> (resolved on a DEDICATED DI scope, so its DbContext never races the event stream)
/// as the ping.
/// </summary>
public static class HeartbeatLoop
{
    /// <summary>
    /// Wait <paramref name="interval"/>, then invoke <paramref name="ping"/>; repeat until
    /// <paramref name="cancellationToken"/> fires. A ping that throws (a transient DB blip) is reported to
    /// <paramref name="onPingError"/> and the loop continues — a missed heartbeat must never kill liveness.
    /// Returns cleanly when cancelled; never surfaces <see cref="OperationCanceledException"/> to the caller.
    /// The first ping is deferred by one interval because the claim already stamped an initial heartbeat.
    /// </summary>
    public static async Task RunAsync(Func<CancellationToken, Task> ping, TimeSpan interval, Action<Exception> onPingError, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(interval, cancellationToken).ConfigureAwait(false);

                try
                {
                    await ping(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    onPingError(ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected: the harness finished or the worker is stopping. Not an error.
        }
    }
}

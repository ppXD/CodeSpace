namespace CodeSpace.Core.Services.Workflows.Dispatch;

/// <summary>
/// Owns the <c>Pending → Enqueued + background-job enqueue</c> step. The row's own
/// <c>Status</c> column IS the queue (PostBoy pattern), and atomic CAS transitions are the
/// single-writer guarantee against double execution.
///
/// <para>Caller contract — only call <c>DispatchAsync</c> AFTER the parent EF transaction
/// (the one that inserted the <c>workflow_run</c> row) has committed. The dispatcher itself
/// owns its own atomic update + the background-job client invocation, and reverts the row
/// to <c>Pending</c> on any throw from the client so the stuck-run reconciler can retry.</para>
///
/// <para>No-double-execution guarantee: two callers (e.g. RunManuallyAsync + a reconciler
/// that finds the same row) both calling <c>DispatchAsync(runId)</c> will race on
/// <c>UPDATE ... WHERE Status = Pending</c>. Postgres returns rows-affected = 1 for one of
/// them and 0 for the other; the loser bails without enqueueing. The eventual engine
/// invocation has its own atomic <c>Enqueued → Running</c> CAS that protects against
/// duplicate Hangfire jobs landing on different workers.</para>
/// </summary>
public interface IWorkflowRunDispatcher
{
    /// <summary>
    /// Atomically transition the run from <c>Pending</c> to <c>Enqueued</c> and hand it to
    /// the background-job client. Returns:
    /// <list type="bullet">
    ///   <item><c>true</c> if THIS caller won the atomic CAS and successfully enqueued — the
    ///         engine will pick up the run via the background-job client.</item>
    ///   <item><c>false</c> if the row was not in <c>Pending</c> state (another caller won
    ///         the race, OR the run is already terminal). This is NOT an error; the loser
    ///         silently returns. Reconciler relies on this idempotence.</item>
    /// </list>
    ///
    /// Throws when the background-job client itself fails to enqueue (e.g. Hangfire storage
    /// unreachable). Before throwing, the dispatcher reverts the row from <c>Enqueued</c>
    /// back to <c>Pending</c> so the next reconciler tick retries.
    /// </summary>
    Task<bool> DispatchAsync(Guid runId, CancellationToken cancellationToken);
}

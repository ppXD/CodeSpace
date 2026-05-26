namespace CodeSpace.Core.Services.Outbox;

/// <summary>
/// Recovers outbox rows whose worker crashed mid-process. Scans for rows with
/// <c>status = Claimed</c> AND <c>lease_until &lt; now()</c> and resets them to
/// <c>Pending</c> so the dispatcher re-claims and retries.
///
/// <para>Without this, a worker that holds a claim and then crashes (OOM, host reboot,
/// network partition) leaves the row Claimed forever — it never re-enters the dispatch
/// loop and the side effect never lands. The reaper is what makes "at-least-once" delivery
/// work across worker failures.</para>
///
/// <para>Idempotent and safe to call concurrently from multiple workers — the UPDATE filters
/// on the same lease_until comparison so two reapers won't unlock the same row twice.</para>
/// </summary>
public interface IOutboxLeaseReaper
{
    /// <summary>
    /// Reset every outbox row where <c>status = Claimed</c> AND <c>lease_until &lt; now()</c>
    /// back to <c>Pending</c>. Returns the number of rows reaped — surface this in logs so
    /// "reaper kept finding rows" is visible.
    /// </summary>
    Task<int> ReapAsync(CancellationToken cancellationToken);
}

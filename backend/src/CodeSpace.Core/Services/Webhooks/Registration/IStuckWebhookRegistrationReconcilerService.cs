namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Recovers <see cref="Persistence.Entities.RepositoryWebhook"/> rows that drifted out of
/// their expected registration-lifecycle trajectory. The dispatcher's CAS + the registrar's
/// CAS give us no-double-execution; the reconciler is what gives us no-stuck-rows.
///
/// <para>Four failure modes covered:</para>
/// <list type="number">
///   <item><b>Stuck Pending</b> (NextAttemptAt &lt; now AND row older than threshold) — the
///       process crashed between BindAsync.SaveChanges and dispatcher.DispatchAsync. Re-dispatch
///       is idempotent (CAS races a normal dispatcher safely).</item>
///   <item><b>Stuck Enqueued</b> (EnqueuedAt older than threshold) — Hangfire lost the job
///       (storage outage, queue mis-routing). Revert to Pending via CAS so the next tick
///       re-dispatches.</item>
///   <item><b>Stuck Registering</b> (RegisteringAt older than threshold) — registrar crashed
///       between the Enqueued→Registering CAS and the provider call response. Revert to
///       Pending; the registrar's idempotency check covers the "provider call already landed"
///       case on the next run.</item>
///   <item><b>Due Failed</b> (NextAttemptAt elapsed) — last attempt failed with backoff;
///       once the backoff elapses, revive to Pending so the next dispatcher tick re-fires.</item>
/// </list>
///
/// <para>Idempotent + safe to call concurrently from multiple replicas because every state
/// transition is an atomic CAS.</para>
/// </summary>
public interface IStuckWebhookRegistrationReconcilerService
{
    Task<StuckWebhookRegistrationReconcileSummary> ReconcileAsync(CancellationToken cancellationToken);
}

/// <summary>Diagnostic summary of one reconcile sweep. Returned for log surfacing + the recurring-job result.</summary>
public sealed record StuckWebhookRegistrationReconcileSummary
{
    public int RedispatchedFromPending { get; init; }
    public int RevertedFromEnqueued { get; init; }
    public int RevertedFromRegistering { get; init; }
    public int RevivedFromFailed { get; init; }

    public int Total => RedispatchedFromPending + RevertedFromEnqueued + RevertedFromRegistering + RevivedFromFailed;
}

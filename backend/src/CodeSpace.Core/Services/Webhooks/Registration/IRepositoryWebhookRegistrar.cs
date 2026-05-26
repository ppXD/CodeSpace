namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Worker that performs the actual provider-side registration. Invoked by Hangfire after
/// <see cref="IRepositoryWebhookRegistrationDispatcher"/> CAS-flips the row to Enqueued.
///
/// <para>The worker:</para>
/// <list type="number">
///   <item>CAS-flips <c>Enqueued → Registering</c> (or no-ops if the row is no longer Enqueued).</item>
///   <item>Loads the webhook + repository + credential, builds the provider context.</item>
///   <item>Idempotency check: <c>FindWebhookByCallbackUrlAsync</c> — if the remote already
///         has a hook at this callback (Hangfire retry / reconciler re-dispatch), reuse it.</item>
///   <item>Otherwise <c>RegisterWebhookAsync</c> on the remote.</item>
///   <item>Atomically writes <c>external_id</c> + flips <c>Registering → Registered</c>.</item>
/// </list>
///
/// <para>Failure path: bump <c>attempts</c>, set <c>last_error</c>, then either flip
/// <c>Registering → Failed</c> (with <c>next_attempt_at = now + backoff</c>) for retryable
/// attempts, or <c>Registering → DeadLettered</c> once <c>attempts &gt;= MaxAttempts</c>.</para>
/// </summary>
public interface IRepositoryWebhookRegistrar
{
    /// <summary>
    /// Run the registration job for one webhook id. Idempotent: a Hangfire retry, a
    /// reconciler re-dispatch, or two workers landing on the same id all resolve to "at
    /// most one remote hook + at most one Registered row".
    /// </summary>
    Task RunAsync(Guid webhookId, CancellationToken cancellationToken);
}

namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Owns the <c>Pending → Enqueued + Hangfire enqueue</c> step for a <c>RepositoryWebhook</c>
/// registration. Mirror of <see cref="Workflows.Dispatch.IWorkflowRunDispatcher"/>: the row's
/// own <c>RegistrationStatus</c> column IS the queue, and atomic CAS transitions are the
/// single-writer guarantee against double remote registration.
///
/// <para>Caller contract — only call <c>DispatchAsync</c> AFTER the parent EF transaction
/// (the one that inserted the <c>repository_webhook</c> row in <c>Pending</c> state) has
/// committed. The dispatcher itself owns its own atomic update + the background-job client
/// invocation, and reverts the row to <c>Pending</c> on any throw from the client so the
/// stuck-webhook reconciler can retry.</para>
///
/// <para>No-double-execution guarantee: two callers (e.g. BindAsync + a reconciler that
/// finds the same row) both calling <c>DispatchAsync(webhookId)</c> will race on
/// <c>UPDATE ... WHERE registration_status = 'Pending'</c>. Postgres returns rows-affected = 1
/// for one of them and 0 for the other; the loser bails without enqueueing. The eventual
/// registrar invocation has its own atomic <c>Enqueued → Registering</c> CAS that protects
/// against duplicate Hangfire jobs landing on different workers. And the provider call inside
/// the registrar is itself idempotent by callback URL — so even a triple-fire ends with
/// exactly one remote hook.</para>
/// </summary>
public interface IRepositoryWebhookRegistrationDispatcher
{
    /// <summary>
    /// Atomically transition the webhook from <c>Pending</c> to <c>Enqueued</c> and hand it
    /// to the background-job client. Returns:
    /// <list type="bullet">
    ///   <item><c>true</c> if THIS caller won the atomic CAS and successfully enqueued — the
    ///         registrar will pick up the webhook via Hangfire.</item>
    ///   <item><c>false</c> if the row was not in <c>Pending</c> state (another caller won
    ///         the race, OR the registration already advanced / was cancelled). This is NOT
    ///         an error; the loser silently returns. Reconciler relies on this idempotence.</item>
    /// </list>
    ///
    /// Throws when the background-job client itself fails to enqueue. Before throwing, the
    /// dispatcher reverts the row from <c>Enqueued</c> back to <c>Pending</c> so the next
    /// reconciler tick retries.
    /// </summary>
    Task<bool> DispatchAsync(Guid webhookId, CancellationToken cancellationToken);
}

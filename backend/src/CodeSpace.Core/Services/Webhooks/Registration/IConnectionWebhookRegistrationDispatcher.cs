namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Owns the <c>Pending → Enqueued + background enqueue</c> step for a <c>ConnectionWebhook</c>.
/// Same contract as <see cref="IRepositoryWebhookRegistrationDispatcher"/>, against the
/// connection-scoped table — read that one for the full account of why the row's own status column
/// IS the queue and why every transition is a CAS.
///
/// <para>Caller contract is likewise the same: only dispatch AFTER the transaction that inserted the
/// <c>Pending</c> row has committed, or the job can run before the row is visible.</para>
/// </summary>
public interface IConnectionWebhookRegistrationDispatcher
{
    /// <summary>
    /// Returns true if THIS caller won the <c>Pending → Enqueued</c> CAS and enqueued the registrar;
    /// false if the row was not Pending (someone else won, or it already advanced). False is not an
    /// error — it is what makes re-dispatch from any caller safe.
    /// </summary>
    Task<bool> DispatchAsync(Guid connectionWebhookId, CancellationToken cancellationToken);
}

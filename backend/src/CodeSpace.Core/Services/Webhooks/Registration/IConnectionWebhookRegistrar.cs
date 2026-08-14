namespace CodeSpace.Core.Services.Webhooks.Registration;

/// <summary>
/// Worker that registers one group / organization hook at the provider. The connection-scoped twin
/// of <see cref="IRepositoryWebhookRegistrar"/>, walking the same states for the same reasons:
/// CAS <c>Enqueued → Registering</c>, find-by-callback-URL before creating, then CAS
/// <c>Registering → Registered</c> with the provider's id written in the same statement.
///
/// <para>Failure path is the same too — an attempt row carrying what the provider answered, then
/// either <c>Failed</c> with backoff or <c>DeadLettered</c> once the attempts are spent. A GitLab
/// Free instance refusing group hooks lands here as a plan refusal that names the plan, with
/// GitLab's own status and body alongside it.</para>
/// </summary>
public interface IConnectionWebhookRegistrar
{
    /// <summary>
    /// Run the registration job for one connection hook. Idempotent under retry, re-dispatch, and
    /// two workers landing on the same id: at most one remote hook, at most one Registered row.
    /// </summary>
    Task RunAsync(Guid connectionWebhookId, CancellationToken cancellationToken);
}

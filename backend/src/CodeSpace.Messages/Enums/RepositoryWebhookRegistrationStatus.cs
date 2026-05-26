namespace CodeSpace.Messages.Enums;

/// <summary>
/// Lifecycle of a remote webhook registration. The row itself IS the durable intent —
/// no separate outbox table. State transitions are atomic CAS so two dispatchers / two
/// workers can race the same row without double-firing the provider call.
///
/// <para>Happy path: <c>Pending → Enqueued → Registering → Registered</c>.</para>
/// <para>Failure paths:</para>
/// <list type="bullet">
///   <item><c>* → Failed → Pending</c> — handler threw; backoff then retry.</item>
///   <item><c>Failed → DeadLettered</c> — exhausted MaxAttempts; needs operator.</item>
///   <item><c>Pending | Enqueued | Registering | Failed → Cancelled</c> — operator
///         unbound the repository while registration was in flight.</item>
/// </list>
/// </summary>
public enum RepositoryWebhookRegistrationStatus
{
    /// <summary>Durable intent persisted alongside the Repository row. Awaiting dispatcher CAS.</summary>
    Pending,

    /// <summary>Dispatcher CAS'd <c>Pending → Enqueued</c> and handed the id to Hangfire.</summary>
    Enqueued,

    /// <summary>Worker CAS'd <c>Enqueued → Registering</c> and is making the provider call.</summary>
    Registering,

    /// <summary>Provider call succeeded; <c>ExternalId</c> + <c>RegisteredAt</c> are set. Terminal happy state.</summary>
    Registered,

    /// <summary>Last attempt failed; <c>NextAttemptAt</c> + <c>LastError</c> are set. Reconciler revives to Pending.</summary>
    Failed,

    /// <summary>Exhausted MaxAttempts. Terminal — needs operator intervention.</summary>
    DeadLettered,

    /// <summary>Unbind happened while registration was still in flight. Terminal.</summary>
    Cancelled
}

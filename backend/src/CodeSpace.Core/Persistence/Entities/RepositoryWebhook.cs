using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// Durable webhook-registration intent + the registered hook itself, in one row.
/// <see cref="RegistrationStatus"/> drives the state machine; once the value reaches
/// <see cref="RepositoryWebhookRegistrationStatus.Registered"/> the row also serves as
/// the production "this hook is alive at the provider" record.
/// </summary>
public class RepositoryWebhook : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid RepositoryId { get; set; }

    /// <summary>
    /// Provider-assigned webhook id. NULL until <see cref="RegistrationStatus"/> reaches
    /// <see cref="RepositoryWebhookRegistrationStatus.Registered"/>. Populated atomically
    /// with the Registering → Registered transition.
    /// </summary>
    public string? ExternalId { get; set; }

    public string CallbackUrl { get; set; } = default!;
    public string SecretEnc { get; set; } = default!;
    public List<string> SubscribedEvents { get; set; } = new();
    public bool Active { get; set; } = true;
    public DateTimeOffset? LastReceivedDate { get; set; }

    /// <summary>Lifecycle state. See <see cref="RepositoryWebhookRegistrationStatus"/>.</summary>
    public RepositoryWebhookRegistrationStatus RegistrationStatus { get; set; } = RepositoryWebhookRegistrationStatus.Pending;

    /// <summary>Number of failed registration attempts. Bumped by the worker on throw; capped by MaxAttempts.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time the dispatcher / reconciler should pick this row up again. Backoff target after a failed attempt.</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last error message from a failed attempt. NULL once Registered or fresh.</summary>
    public string? LastError { get; set; }

    /// <summary>Set when the dispatcher CAS'd Pending → Enqueued.</summary>
    public DateTimeOffset? EnqueuedAt { get; set; }

    /// <summary>Set when the worker CAS'd Enqueued → Registering. The reconciler revives rows whose RegisteringAt is older than the stuck-threshold.</summary>
    public DateTimeOffset? RegisteringAt { get; set; }

    /// <summary>Set when the worker CAS'd Registering → Registered. Identifies the row as "live at the provider".</summary>
    public DateTimeOffset? RegisteredAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public Repository Repository { get; set; } = default!;
}

using CodeSpace.Messages.Enums;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One group (GitLab) or organization (GitHub) hook, registered above the repository. Sibling of
/// <see cref="RepositoryWebhook"/> in every respect except what it is keyed on: a group hook has
/// no single repository to belong to, so the row hangs off the provider instance and the
/// <see cref="OwnerPath"/> it was registered on.
///
/// <para>The lifecycle is the same one, deliberately — <see cref="RegistrationStatus"/> is the
/// same <see cref="RepositoryWebhookRegistrationStatus"/> vocabulary driven by the same CAS
/// transitions, so an operator who has read one table can read the other. The type keeps its
/// historical name; the states it enumerates were never repository-specific.</para>
/// </summary>
public class ConnectionWebhook : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    public Guid ProviderInstanceId { get; set; }

    /// <summary>
    /// GitLab group full path or GitHub organization login the hook sits on — the bound
    /// repository's <c>NamespacePath</c>, which is exactly that value for both providers. One row
    /// per owner: a connection can span several groups and a provider hook only covers one.
    /// </summary>
    public string OwnerPath { get; set; } = default!;

    /// <summary>
    /// The credential that registered the hook, recorded rather than re-derived: a connection has
    /// several and deleting the hook has to use the identity that created it.
    /// </summary>
    public Guid CredentialId { get; set; }

    /// <summary>
    /// Provider-assigned hook id. NULL until <see cref="RegistrationStatus"/> reaches
    /// <see cref="RepositoryWebhookRegistrationStatus.Registered"/>, written atomically with
    /// that transition.
    /// </summary>
    public string? ExternalId { get; set; }

    public string CallbackUrl { get; set; } = default!;

    /// <summary>
    /// This hook's own encrypted secret. Inbound group deliveries verify against THIS value — the
    /// repository a delivery is about is resolved from the payload only after that check passes.
    /// </summary>
    public string SecretEnc { get; set; } = default!;

    public List<string> SubscribedEvents { get; set; } = new();
    public bool Active { get; set; } = true;
    public DateTimeOffset? LastReceivedDate { get; set; }

    /// <summary>Lifecycle state. See <see cref="RepositoryWebhookRegistrationStatus"/>.</summary>
    public RepositoryWebhookRegistrationStatus RegistrationStatus { get; set; } = RepositoryWebhookRegistrationStatus.Pending;

    /// <summary>Number of failed registration attempts. Bumped by the registrar on throw; capped by MaxAttempts.</summary>
    public int Attempts { get; set; }

    /// <summary>Earliest time this row should be picked up again. Backoff target after a failed attempt.</summary>
    public DateTimeOffset NextAttemptAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Last error message from a failed attempt. NULL once Registered or fresh.</summary>
    public string? LastError { get; set; }

    /// <summary>Set when the dispatcher CAS'd Pending → Enqueued.</summary>
    public DateTimeOffset? EnqueuedAt { get; set; }

    /// <summary>Set when the registrar CAS'd Enqueued → Registering.</summary>
    public DateTimeOffset? RegisteringAt { get; set; }

    /// <summary>Set when the registrar CAS'd Registering → Registered. Identifies the row as "live at the provider".</summary>
    public DateTimeOffset? RegisteredAt { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    public ProviderInstance ProviderInstance { get; set; } = default!;
    public Credential Credential { get; set; } = default!;
}

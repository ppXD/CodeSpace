namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// One operator-authored DEPLOYMENT default for a routed data class — where a team should be pointed for that class.
///
/// <para><b>Nothing in this build reads it.</b> No team resolves storage through it, no route is created from it, and
/// no byte moves because of it. The intended reader is the materializer lane, which will turn one of these rows into a
/// team's own <see cref="StorageCredential"/> / <see cref="StorageProfile"/> / <see cref="StorageRoute"/> and record
/// what it did in <see cref="StorageDefaultMaterialization"/>. No team inherits anything from this row today.</para>
/// </summary>
public class StorageDefault : IEntity<Guid>, IAuditable
{
    public Guid Id { get; set; }

    /// <summary>The exact routed data class this template describes, for example <c>workflow-artifact/v1</c>. Unique.</summary>
    public string DataClassTypeKey { get; set; } = string.Empty;

    /// <summary>
    /// Monotonic edit counter stamped into <see cref="StorageDefaultMaterialization.SourceRevision"/>, so a team
    /// materialized from an older template can be told apart from a current one. This is deliberately not an
    /// append-only ledger the way profile and credential revisions are: nothing durable pins a template revision, and
    /// the byte-exact content a team received is preserved in the immutable profile revision the materializer writes.
    /// </summary>
    public int Revision { get; set; } = 1;

    /// <summary>Canonical major-versioned provider key, for example <c>aliyun-oss/v1</c>.</summary>
    public string ProviderTypeKey { get; set; } = string.Empty;

    /// <summary>Provider config EXCLUDING every namespace field. Never contains values from the provider's SecretSchema.</summary>
    public string NonSecretConfigJson { get; set; } = "{}";

    /// <summary>
    /// A ROOT, never a finished namespace. The materializer MUST append a per-team segment before this reaches a
    /// team's profile revision.
    ///
    /// <para>Object keys carry no team segment — <c>ArtifactStore.Routing.cs</c> builds
    /// <c>workflow-artifacts/{aa}/{bb}/{sha256}</c> — so tenant isolation rests entirely on each team's profile
    /// namespace differing. Purge is strictly per-team and deletes by the row's own ETag, and identical bytes produce
    /// an identical object with an identical ETag; two teams sharing a namespace therefore means one team's reaper
    /// deletes an object another team's location still marks Available. Silent cross-team data loss.</para>
    /// </summary>
    public string NamespaceRoot { get; set; } = string.Empty;

    /// <summary>The instance-scope encrypted envelope this template resolves through, or null for a provider that needs none.</summary>
    public Guid? CredentialId { get; set; }

    public StorageDefaultAdoptionPolicy AdoptionPolicy { get; set; } = StorageDefaultAdoptionPolicy.Explicit;

    /// <summary>A disabled template is inert: the materializer must skip it. Templates are never deleted.</summary>
    public bool IsEnabled { get; set; }

    public DateTimeOffset CreatedDate { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset LastModifiedDate { get; set; }
    public Guid LastModifiedBy { get; set; }

    /// <summary>Npgsql xmin optimistic-concurrency token for template edits.</summary>
    public uint Xmin { get; set; }

    public StorageDefaultCredential? Credential { get; set; }
}

/// <summary>
/// How a team comes to be materialized onto a deployment default. Stated per data class in the template itself, so a
/// data class added later cannot be routed without choosing one.
/// </summary>
public enum StorageDefaultAdoptionPolicy
{
    /// <summary>
    /// The team is materialized on its first write, the way local Agent Run log routes are bootstrapped today. Only
    /// safe for a class that has NO local home: such a class refuses writes until it is cut over, so cutting it over
    /// takes nothing away from the team.
    /// </summary>
    Automatic,

    /// <summary>
    /// Materialized ONLY when that team's admin adopts it. Never automatic.
    ///
    /// <para>Because once a team's route for a class is Active, that team is <b>permanently off local disk</b> for
    /// that class: <c>StorageRouteRules.EnsureTransition</c> refuses any transition back to Draft, Retired is
    /// terminal, and a route cannot be deleted. "Overridable" here means the route can be repointed at another
    /// destination — NOT that it can be returned to local. Auto-adopting that would commit every new team
    /// irreversibly without anyone choosing it.</para>
    /// </summary>
    Explicit,
}

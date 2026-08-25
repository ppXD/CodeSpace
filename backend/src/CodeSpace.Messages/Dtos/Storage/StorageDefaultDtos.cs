using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>
/// One deployment-wide storage default. Nothing in this build consumes the template — the materializer lane is the
/// intended reader — so this describes operator configuration, never a destination any team is using today.
/// </summary>
public sealed record StorageDefaultSummary
{
    public required Guid Id { get; init; }
    public required string DataClassTypeKey { get; init; }
    public required int Revision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required StorageDefaultAdoptionPolicyValue AdoptionPolicy { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool HasCredential { get; init; }
    public string? CredentialSafeHint { get; init; }
    public required uint Xmin { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
}

/// <summary>
/// The full template. Ciphertext is never projected — only whether a credential is attached and its pre-sanitized
/// hint, matching the write-only-secret convention <c>StorageCredentialMetadata</c> already follows.
/// </summary>
public sealed record StorageDefaultDetail
{
    public required Guid Id { get; init; }
    public required string DataClassTypeKey { get; init; }
    public required int Revision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement NonSecretConfig { get; init; }

    /// <summary>
    /// A ROOT, never a finished namespace: the materializer MUST append a per-team segment before this reaches a
    /// team's storage profile. Object keys carry no team segment, so two teams sharing one namespace means one team's
    /// reaper deletes objects another team still holds.
    /// </summary>
    public required string NamespaceRoot { get; init; }

    public required StorageDefaultAdoptionPolicyValue AdoptionPolicy { get; init; }
    public required bool IsEnabled { get; init; }
    public required bool HasCredential { get; init; }
    public string? CredentialSafeHint { get; init; }
    public required uint Xmin { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required Guid CreatedBy { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
    public required Guid LastModifiedBy { get; init; }
}

/// <summary>
/// How a team comes to be materialized onto a deployment default.
///
/// <para><c>Explicit</c> exists because once a team's route for a data class is Active, that team is permanently off
/// local disk for that class: the route lifecycle refuses any transition back to Draft, Retired is terminal, and a
/// route cannot be deleted. "Overridable" means the route can be repointed at another destination, NOT that it can be
/// returned to local.</para>
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageDefaultAdoptionPolicyValue
{
    /// <summary>Materialized on a team's first write. Only lawful for a data class that has no local home of its own.</summary>
    Automatic = 0,

    /// <summary>Materialized only when that team's admin adopts it. Never automatic.</summary>
    Explicit = 1,
}

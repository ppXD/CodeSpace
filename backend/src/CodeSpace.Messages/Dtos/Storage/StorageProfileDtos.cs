using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeSpace.Messages.Dtos.Storage;

/// <summary>
/// What a probe last saw at this profile's destination, or null when none has run.
///
/// <para>Null is a real answer and a screen must render it as one: "nobody has checked" is not "working". The
/// distinction matters most for a hand-built profile, where nothing probes automatically before its route goes Active.</para>
/// </summary>
public sealed record StorageProfileHealthSummary
{
    public required StorageProfileProbeStatusValue Status { get; init; }

    /// <summary>True only when the probe PUT and discarded a real object. A passing read-only probe qualifies reachability, not that a run's bytes will land.</summary>
    public required bool WriteVerified { get; init; }

    /// <summary>The revision that was exercised. Behind the profile's current revision means this describes a destination the profile has since left.</summary>
    public required int ProfileRevision { get; init; }

    public StorageProfileProbeFailureStageValue? FailureStage { get; init; }
    public StorageProfileProbeFailureCodeValue? FailureCode { get; init; }
    public required long LatencyMilliseconds { get; init; }
    public required DateTimeOffset ObservedAt { get; init; }
}

public sealed record StorageProfileSummary
{
    public required Guid Id { get; init; }
    public required string StableName { get; init; }
    public required StorageProfileStateValue State { get; init; }
    public required int CurrentRevision { get; init; }
    public required uint Xmin { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }

    /// <summary>The last probe observation, or null when nothing has ever checked this destination.</summary>
    public StorageProfileHealthSummary? Health { get; init; }
}

public sealed record StorageProfileDetail
{
    public required Guid Id { get; init; }
    public required string StableName { get; init; }
    public required StorageProfileStateValue State { get; init; }
    public required int CurrentRevision { get; init; }
    public required uint Xmin { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required Guid CreatedBy { get; init; }
    public required DateTimeOffset LastModifiedDate { get; init; }
    public required Guid LastModifiedBy { get; init; }
    public required IReadOnlyList<StorageProfileRevisionDetail> Revisions { get; init; }
}

public sealed record StorageProfileRevisionDetail
{
    public required Guid Id { get; init; }
    public required int Revision { get; init; }
    public required string ProviderTypeKey { get; init; }
    public required JsonElement NonSecretConfig { get; init; }
    public string? CredentialRef { get; init; }
    public required string NamespaceFingerprint { get; init; }
    public required DateTimeOffset CreatedDate { get; init; }
    public required Guid CreatedBy { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageProfileStateValue
{
    Draft = 0,
    Active = 1,
    Disabled = 2,
    Retired = 3,
}

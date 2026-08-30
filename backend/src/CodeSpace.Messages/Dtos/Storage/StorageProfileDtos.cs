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

/// <summary>
/// Whether the artifact bytes this team has already written are still where they were put.
///
/// <para>A separate question from <see cref="StorageProfileHealthSummary"/>, which describes whether a destination can
/// be reached right now. A destination can answer perfectly while every object written to it last year is gone, and
/// nothing about probing it would reveal that.</para>
/// </summary>
public sealed record PlacementIntegritySummary
{
    /// <summary>Placements the destination no longer holds. These are known losses: something a reader will not get.</summary>
    public required int Missing { get; init; }

    /// <summary>Placements whose destination now holds something that is not the recorded object.</summary>
    public required int Corrupt { get; init; }

    /// <summary>Placements believed good. The population the two counts above should be read against.</summary>
    public required int Available { get; init; }

    /// <summary>
    /// When the least recently confirmed Available placement was last known good, or null when the team has stored
    /// nothing.
    ///
    /// <para>Never null while <see cref="Available"/> is non-zero: the schema requires an Available placement to carry
    /// a <c>verified_at</c>, which starts life as the instant its bytes were written and moves forward each time the
    /// verifier confirms them. So this reads the same way whether or not a sweep has reached it yet — the moment that
    /// placement was last actually observed to be there.</para>
    /// </summary>
    public DateTimeOffset? OldestVerifiedAt { get; init; }
}

/// <summary>
/// What a placement's record says about the bytes it names. Mirrors the persistence enum, whose ordinals are pinned
/// by <c>ck_artifact_location_state</c>; the two are compared by a schema test rather than shared, because Messages
/// cannot see Core.
/// </summary>
public enum ArtifactLocationStateValue
{
    Pending = 0,
    Available = 1,
    Missing = 2,
    Corrupt = 3,
    Deleting = 4,
    Deleted = 5,
    Failed = 6,
    Purged = 7,
}

/// <summary>One placement still recorded under a storage profile.</summary>
public sealed record ProfilePlacementSummary
{
    public required Guid LocationId { get; init; }
    public required Guid ArtifactObjectId { get; init; }
    public required ArtifactLocationStateValue State { get; init; }
    public required string ObjectKey { get; init; }

    /// <summary>Which revision of the profile placed it. A profile that has been re-pointed holds rows under several.</summary>
    public required int ProfileRevision { get; init; }

    public long? SizeBytes { get; init; }
    public DateTimeOffset? VerifiedAt { get; init; }
    public string? LastErrorCode { get; init; }
}

/// <summary>How many placements a profile holds in one state, and how many bytes they account for.</summary>
public sealed record ProfilePlacementTotal
{
    public required ArtifactLocationStateValue State { get; init; }
    public required int Count { get; init; }
    public required long SizeBytes { get; init; }
}

public sealed record ProfilePlacementPage
{
    public required IReadOnlyList<ProfilePlacementSummary> Items { get; init; }
    public string? NextCursor { get; init; }
}

/// <summary>What one bounded pass of abandoning a profile's placements did.</summary>
public sealed record ProfileAbandonmentSummary
{
    /// <summary>Placements this pass asked the destination about.</summary>
    public required int Examined { get; init; }

    /// <summary>Records closed: the destination proved it cannot serve them.</summary>
    public required int Abandoned { get; init; }

    /// <summary>Placements the destination SERVED. Left exactly as they were — this is the refusal that makes the operation safe.</summary>
    public required int StillServed { get; init; }

    /// <summary>Placements whose destination gave no usable answer. Left as they were, to be asked again.</summary>
    public required int Unanswered { get; init; }

    /// <summary>Unreleased placements still under the profile after this pass. Non-zero means call again.</summary>
    public required int Remaining { get; init; }

    /// <summary>
    /// The problem code that stopped the pass before its batch was done, or null when the whole batch was examined.
    ///
    /// <para>Set when one answer came back for so much of the batch that it is a statement about the destination
    /// rather than about any object under it. Without it a short <c>Examined</c> is silent, and an operator reading
    /// "examined 13, abandoned 0" cannot tell a pass that stopped from a profile that only had 13 placements.</para>
    /// </summary>
    public string? StoppedBy { get; init; }
}

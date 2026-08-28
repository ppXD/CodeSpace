using CodeSpace.Messages.Dtos.Storage;

namespace CodeSpace.Core.Persistence.Entities;

/// <summary>
/// What a probe last observed about one storage profile's destination.
///
/// <para>Deliberately NOT columns on <see cref="StorageProfile"/>. That row carries the optimistic-concurrency token
/// every operator edit checks, so a background probe writing into it would advance the token and fail an operator's
/// next save with a conflict nobody caused. Health is observed ABOUT a profile, never BY it.</para>
///
/// <para>One row per profile, overwritten. A settings screen asks "does my storage work right now" — a history would
/// answer a question nobody has asked yet, and would need a retention policy of its own.</para>
/// </summary>
public class StorageProfileHealth
{
    public Guid TeamId { get; set; }
    public Guid StorageProfileId { get; set; }

    /// <summary>The revision the probe exercised. Compare against the profile's current revision: health for an older revision describes a destination the profile has since left.</summary>
    public int ProfileRevision { get; set; }

    public StorageProfileProbeStatusValue Status { get; set; }

    /// <summary>True when the probe PUT and discarded a real object. A read-only pass qualifies the credential's ability to list, which is a weaker claim than "a run's bytes will land here".</summary>
    public bool WriteVerified { get; set; }

    public StorageProfileProbeFailureStageValue? FailureStage { get; set; }
    public StorageProfileProbeFailureCodeValue? FailureCode { get; set; }
    public long LatencyMs { get; set; }
    public DateTimeOffset ObservedAt { get; set; }

    public StorageProfile Profile { get; set; } = default!;
}

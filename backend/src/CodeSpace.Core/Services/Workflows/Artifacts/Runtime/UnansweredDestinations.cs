using CodeSpace.Core.Persistence.Entities;

namespace CodeSpace.Core.Services.Workflows.Artifacts.Runtime;

/// <summary>
/// The destinations ONE verification pass has already found unable to answer FOR THEMSELVES.
///
/// <para>Membership is not "this row came back inconclusive". A row can end inconclusive because of the object it named
/// — a key the destination refuses, one it does not support, one request it throttled — and none of that is true of the
/// destination's other placements. Only an answer the destination gave about ITSELF belongs here, because only that one
/// is the same answer for every row pinned to it.</para>
///
/// <para>A destination is identified by the pin its placements carry — the team, and the storage profile revision the
/// row was written under — because that pin is exactly what the broker resolves into a driver. Two rows sharing it are
/// two rows about the same bucket, mount or volume, so one refusal answers for both. A revision that repoints the
/// profile is a different pin and therefore a different destination, which is the conservative direction: it can cost
/// an extra round trip, never a row dropped that should have been asked.</para>
///
/// <para>Only NEGATIVE verdicts belong here, and the type deliberately cannot express the other kind. A destination
/// that failed to answer for one row fails for the rest, and re-asking spends the whole batch on it at a round trip
/// per row while every healthy destination goes unchecked. Remembering that a destination DID answer is unsafe at any
/// scale: that answer is what licenses a demotion, and a mount which disappears mid-pass has to stop licensing them
/// on the very next row rather than on the next hour's pass.</para>
/// </summary>
public sealed class UnansweredDestinations
{
    private readonly HashSet<(Guid TeamId, Guid StorageProfileRevisionId)> _pins = [];

    /// <summary>Whether the destination behind this location has already failed to answer in this pass.</summary>
    public bool Contains(ArtifactLocation location) => _pins.Contains(Pin(location));

    /// <summary>Files the destination behind this location as one that did not answer, which drops its remaining rows from this pass.</summary>
    public void Add(ArtifactLocation location) => _pins.Add(Pin(location));

    private static (Guid TeamId, Guid StorageProfileRevisionId) Pin(ArtifactLocation location) => (location.TeamId, location.StorageProfileRevisionId);
}

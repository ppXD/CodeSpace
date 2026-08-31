namespace CodeSpace.Messages.Dtos.Storage;

public static class LegacyPlacementSurveyLimits
{
    /// <summary>
    /// Most pre-CAS rows one report-only pass may examine, whatever the caller asks for.
    ///
    /// <para>Protects the destination and the request from a population no operator sized: a deployment's pre-CAS
    /// tier can run to millions of rows, and one HEAD each is a request that never returns.
    /// <see cref="LegacyPlacementSurvey.Found"/> still reports the whole population, so a capped pass says plainly
    /// how much of it it saw.</para>
    /// </summary>
    public const int MaxRowsPerPass = 1000;
}

/// <summary>Why a survey could establish nothing, or <c>None</c> when it ran.</summary>
public enum LegacyPlacementSurveyRefusalValue
{
    /// <summary>The survey ran. Its counts are the answer.</summary>
    None = 0,

    /// <summary>No profile of that id in this team, or none with a revision to read a configuration from.</summary>
    ProfileMissing = 1,

    /// <summary>The profile's provider has no pre-CAS layout at all, so it can name none of these rows.</summary>
    ProviderHasNoLegacyLayout = 2,

    /// <summary>The destination could not be opened, so no row got as far as being asked about.</summary>
    DestinationUnavailable = 3,
}

/// <summary>
/// What one report-only pass over a team's pre-CAS artifact rows established against one storage profile. It writes
/// nothing: no <c>artifact_location</c> is minted, no <c>workflow_artifact</c> is relinked, no byte moves.
///
/// <para>Read <see cref="Resolved"/> before anything else. It counts rows whose recorded locator the profile's own
/// layout REPRODUCED, which is the only ground truth a candidate layout has — and it is what separates "the
/// destination lost the bytes" (resolved, then unconfirmed) from "this profile cannot name them at all" (unresolved).
/// The two look identical in a confirmation count and mean opposite things.</para>
/// </summary>
public sealed record LegacyPlacementSurvey
{
    public required Guid ProfileId { get; init; }

    /// <summary>The provider the profile is configured for, or null when no profile revision could be read.</summary>
    public string? ProviderTypeKey { get; init; }

    /// <summary>Every pre-CAS row this team holds — the whole population, whatever this pass had budget to examine.</summary>
    public required int Found { get; init; }

    /// <summary>How many of <see cref="Found"/> this bounded pass actually examined.</summary>
    public required int Surveyed { get; init; }

    /// <summary>Examined rows whose recorded locator the profile's layout reproduced exactly.</summary>
    public required int Resolved { get; init; }

    /// <summary>Resolved rows the destination confirmed it still holds.</summary>
    public required int Confirmed { get; init; }

    /// <summary>Resolved rows the destination did NOT confirm. The bytes are the question here, not the key.</summary>
    public required int Unconfirmed { get; init; }

    /// <summary>Bytes across <see cref="Confirmed"/>, as the destination reported them — never as the row claims them.</summary>
    public required long ConfirmedSizeBytes { get; init; }

    /// <summary>
    /// Whether a later minting pass may run against this profile at all: true only when the layout NAMED rows and the
    /// destination ANSWERED for some of them.
    ///
    /// <para>False whenever nothing resolved, and that refusal is the point: a destination that resolves nothing is a
    /// key-mapping bug far more often than a destination that lost everything, and a minting pass that believed it
    /// would write a row for every artifact naming a key holding nothing.</para>
    ///
    /// <para>False just as firmly when everything resolved and nothing was confirmed — an unmounted or emptied root
    /// reaches that identical end state, having proved only that the key mapping is right.</para>
    /// </summary>
    public required bool AdoptionAdmissible { get; init; }

    /// <summary>Why the pass established nothing, or <c>None</c> when it ran and the counts are the answer.</summary>
    public required LegacyPlacementSurveyRefusalValue Refusal { get; init; }
}

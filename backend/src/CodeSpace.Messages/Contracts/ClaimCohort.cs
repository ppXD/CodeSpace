namespace CodeSpace.Messages.Contracts;

/// <summary>
/// The cohort behind a claim-board row as a reader sees it: the facts of <see cref="LaunchCohortDescriptor"/>, with
/// the paying team named only to a reader entitled to it.
///
/// <para>The board is platform-wide, but the receipt behind a row was paid for by one team — the round ran under that
/// team's header and its model credential. Which team that was is the paying team's business, and its id was the one
/// ingredient other cross-team probes lacked.</para>
/// </summary>
public sealed record ClaimCohort
{
    /// <summary>The team whose round earned the standing — null unless the reader is in that team or is a global admin.</summary>
    public Guid? TeamId { get; init; }

    /// <summary>The operating mode (a <c>RunModeKeys</c> value).</summary>
    public required string Mode { get; init; }

    /// <summary>What kind of traffic the standing covers (e.g. <see cref="LaunchCohortDescriptor.InternalQualificationTier"/>).</summary>
    public required string Tier { get; init; }

    public required int CompletionPolicyVersion { get; init; }

    public string? ProjectionKind { get; init; }
}

namespace CodeSpace.Messages.Contracts;

/// <summary>
/// One facet a sweep READ as abandoned, together with the silence cutoff it read it under — the claim a conditional
/// un-stating is asked to prove still holds before it writes.
///
/// <para><b>Why the cutoff travels with the row.</b> A sweep never observes abandonment; it infers it from a row it
/// selected in an EARLIER transaction, and by the time the write runs the producer that looked dead may have committed
/// its accounting. Carrying <see cref="SettledBefore"/> is what lets the write re-ask the exact question the read
/// asked, rather than a fresher one that would answer differently for reasons that have nothing to do with the row.</para>
///
/// <para><see cref="Facet"/> is one of <see cref="WorkflowRunDataOwnerKinds"/>, the same owner nouns every statement
/// about a run's record is made over.</para>
/// </summary>
public sealed record RunDataAbandonedExpectation
{
    public required Guid TeamId { get; init; }

    public required Guid WorkflowRunId { get; init; }

    public required string Facet { get; init; }

    /// <summary>The instant the selecting read required the facet to have gone unadvanced since. Re-checked by the write, so a facet that moved in between keeps what it stated.</summary>
    public required DateTimeOffset SettledBefore { get; init; }
}

namespace CodeSpace.Messages.Contracts;

/// <summary>
/// One fold into a run's completeness statement for ONE facet of its record: how many records the producer UNDERTOOK
/// to capture, how many of them became durable, and whether any that landed reached storage masked. Both counts are
/// DELTAS added to what the facet already states, never totals.
///
/// <para><b>Why the two counts are separate rather than one "captured" number.</b> They are advanced in separate calls
/// on purpose: a producer declares its expectation BEFORE the records land and states their presence AFTER, so an
/// accounting lost in between leaves present below expected — which is not complete. Advancing both together would
/// leave them equally short and the facet would read complete over records nobody counted. Nothing here enforces that
/// order; a delta cannot see the write it describes. What enforces it is the producer's own tests.</para>
///
/// <para>A delta only ever ADDS. <c>workflow_run_data_manifest_advance</c> refuses a negative one, because a count a
/// writer can walk down is a complete verdict reachable by subtraction — and nothing compares these counts to the
/// plane they describe, which is the full scan the materialized statement exists to avoid.</para>
///
/// <para><see cref="Facet"/> is one of <see cref="WorkflowRunDataOwnerKinds"/>: the contract's own owner nouns, so a
/// statement is always matched to the plane whose records it counts.</para>
/// </summary>
public sealed record RunDataFacetAdvance
{
    public required Guid TeamId { get; init; }

    public required Guid WorkflowRunId { get; init; }

    public required string Facet { get; init; }

    /// <summary>How many more records of this facet the producer undertook to capture. Non-negative.</summary>
    public required long Expected { get; init; }

    /// <summary>How many more of them became durable records. Non-negative.</summary>
    public required long Present { get; init; }

    /// <summary>Whether any record this delta counts as present reached storage with secret spans masked, which reaches the redacted arm of a complete verdict rather than the verbatim one.</summary>
    public bool Masked { get; init; }
}

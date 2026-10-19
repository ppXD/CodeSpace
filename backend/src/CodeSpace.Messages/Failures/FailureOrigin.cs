namespace CodeSpace.Messages.Failures;

/// <summary>
/// Where a failure happened when it did not happen in the run whose record carries it: the sub-workflow run that met the provider
/// failure, and the node that ended it. A <c>flow.subworkflow</c> node fails with the failure its child run ended on, so the parent's
/// own record can say what is wrong; this is the pointer that says which child run that was, so the parent's page can name it and link to it.
///
/// <para>The DEEPEST run that met the failure, never an intermediate one: a failure that travelled through nested sub-workflows is
/// carried by every level with the origin it was first stamped with. Only identifiers and a time are kept — the run, the node and when
/// that run recorded the failure — because what the run is called and what it was launched against are read live, under the asker's team, when the remedy is built.</para>
///
/// <para>The time matters because the parent that carries the failure is resumed only once every sub-workflow it is waiting on has finished, so its own
/// record can be much later than the failure; whether the credential has been fixed since is measured from the failure, not from the resume.</para>
/// </summary>
public sealed record FailureOrigin
{
    /// <summary>The run whose own record names the failure — the child, or the deepest child of a chain of them.</summary>
    public required Guid RunId { get; init; }

    /// <summary>The node that ended that run. Null where the run's own record carried the failure (a run that could not start) and no node did.</summary>
    public string? NodeId { get; init; }

    /// <summary>When that run recorded the failure. Null in a record written before it was kept, which is then judged from the time of the record that carries it, as it always was.</summary>
    public DateTimeOffset? At { get; init; }
}

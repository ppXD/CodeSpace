namespace CodeSpace.Messages.Tasks.Phases;

/// <summary>
/// The shared header the projector hands each <c>IRunPhaseSource</c> — the already-resolved (run, team) the source
/// reads its substrate slice with. The projector does the tenancy precheck ONCE and passes the resolved team here
/// so a source never re-resolves tenancy off the wire (the team is from <c>ICurrentTeam</c>, never a request body).
/// </summary>
public sealed record RunPhaseContext
{
    public required Guid RunId { get; init; }
    public required Guid TeamId { get; init; }

    /// <summary>Whether a source that reads the run detail should show the LINEAGE-MERGED picture (default) or scope STRICTLY to this run's own cells. The Session Room's per-attempt view sets this false so each attempt shows ONLY its own flow, never the latest attempt's merged in.</summary>
    public bool MergeLineage { get; init; } = true;
}

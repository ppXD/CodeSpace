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
}

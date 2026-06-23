namespace CodeSpace.Messages.Tasks.Timeline;

/// <summary>
/// The shared header the projector hands each <c>IRunTimelineSource</c> — the already-resolved (run, team) the
/// source reads its ledger slice with. The projector does the tenancy precheck ONCE and passes the resolved team
/// here, so a source never re-resolves tenancy off the wire (the team is from <c>ICurrentTeam</c>, never a body).
/// </summary>
public sealed record RunTimelineContext
{
    public required Guid RunId { get; init; }
    public required Guid TeamId { get; init; }
}

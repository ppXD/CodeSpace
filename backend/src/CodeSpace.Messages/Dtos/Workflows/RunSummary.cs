namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// True scoped counts for the runs cockpit cards — each is a COUNT over the team's runs narrowed by the SAME scope
/// filter the bar applies, not a tally of a loaded page. So "nothing selected" is the genuine superset and a filter
/// only ever narrows. <see cref="SuspendedNeedingReview"/> is the run half of the Needs-attention card: Suspended runs
/// a human must look at (no pending decision already covers them); the other half is the cross-grain decision queue.
/// <see cref="Today"/> counts runs created since the caller's local start-of-day (passed in, since the day boundary is
/// the user's timezone, not the server's).
/// </summary>
public sealed record RunSummary
{
    public required int Live { get; init; }
    public required int Failed { get; init; }
    public required int Suspended { get; init; }
    public required int SuspendedNeedingReview { get; init; }
    public required int Today { get; init; }
}

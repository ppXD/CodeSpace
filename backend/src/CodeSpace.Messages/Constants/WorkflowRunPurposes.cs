namespace CodeSpace.Messages.Constants;

/// <summary>
/// Canonical <c>purpose</c> values for <c>workflow_run.purpose</c> — an open string marking a run that is NOT a
/// genuine operator launch, so it can be excluded from operator-facing indexes by default (the team Runs index's
/// <c>WorkflowService.CollapseToLatestPerLineage</c>) without conflating with <see cref="WorkflowRunSourceTypes"/>
/// (every task launch — real or qualification — stages through the SAME <c>"snapshot"</c> source). NULL (the
/// overwhelmingly common case) is a genuine launch; a non-null value is never set by any real launch surface.
/// Pinned by <c>WorkflowRunPurposesTests</c>.
/// </summary>
public static class WorkflowRunPurposes
{
    /// <summary>A TaskLaunch qualification/benchmark cell (<c>TaskLaunchBenchmarkCellRunner</c>) — launches as the team's real Owner but is not real work; excluded from the team Runs index by default.</summary>
    public const string Qualification = "qualification";
}

namespace CodeSpace.Messages.Constants;

/// <summary>
/// Canonical <c>purpose</c> values for <c>workflow_run.purpose</c> — an open string marking a run that is NOT a
/// genuine operator launch, so it can be excluded from operator-facing indexes by default (the team Runs index's
/// <c>WorkflowService.CollapseToLatestPerLineage</c>) without conflating with <see cref="WorkflowRunSourceTypes"/>
/// (every task launch — real or qualification — stages through the SAME <c>"snapshot"</c> source). NULL (the
/// overwhelmingly common case) is a genuine launch; a non-null value is never set by any real launch surface.
/// Pinned by <c>WorkflowRunPurposesTests</c>.
///
/// <para><b>Not a cost/budget exemption.</b> This column is read ONLY by the team Runs index — the LLM-plane budget
/// ledger and team cost aggregates (<c>BudgetLedger</c>, <c>TeamCostService</c>) key purely off <c>workflow_run.id</c>
/// / team, never off this column. A purpose-marked (e.g. qualification) run's real provider spend is intentionally
/// still reserved, settled and counted in those aggregates exactly like any other run — hiding a run from the
/// Runs index must never be read as hiding its spend.</para>
/// </summary>
public static class WorkflowRunPurposes
{
    /// <summary>A TaskLaunch qualification/benchmark cell (<c>TaskLaunchBenchmarkCellRunner</c>) — launches as the team's real Owner but is not real work; excluded from the team Runs index by default.</summary>
    public const string Qualification = "qualification";
}

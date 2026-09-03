namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Writes ONE terminal run's durable north-star row (<c>run_scorecard</c>). Called from the completion-shadow
/// sweep — the seam every terminal contract-era run already passes through, and the seam whose own
/// <c>CompletionAssessmentRecord</c> carries the metric@1 verdict this row's <c>solved</c> bit reads — and from the
/// bounded backfill for runs that terminalized before the table existed.
///
/// <para>Idempotent by run (the table is unique on <c>workflow_run_id</c>): a replayed sweep re-projects the same
/// settled facts onto the same row rather than appending a second opinion. Observation-only — nothing in the engine
/// reads the row, so a failed write delays a trend point and never a run.</para>
/// </summary>
public interface IRunScorecardWriter
{
    /// <summary>Project + upsert the run's row. False when the run is not a candidate (not found, not terminal, or pre-protocol — a legacy run is visible in the rollup's <c>LegacyRuns</c> and never scored).</summary>
    Task<bool> WriteAsync(Guid workflowRunId, Guid teamId, CancellationToken cancellationToken);
}

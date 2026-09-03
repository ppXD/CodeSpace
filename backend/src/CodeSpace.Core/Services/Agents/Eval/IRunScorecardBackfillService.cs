namespace CodeSpace.Core.Services.Agents.Eval;

/// <summary>
/// Fills the durable <c>run_scorecard</c> row for terminal contract-era runs that lack one. The PRIMARY writer is
/// the completion-shadow sweep, which every new terminal run passes through; this exists for the runs that
/// terminalized before the table did — without it a freshly-deployed trend would start at "no history" and stay
/// there for every already-settled run, because a settled run never becomes a shadow candidate again.
/// </summary>
public interface IRunScorecardBackfillService
{
    /// <summary>Project up to <paramref name="batchSize"/> row-less terminal runs. Idempotent: a run WITH a row is not a candidate, so repeated ticks only shrink the backlog. Returns how many rows were written.</summary>
    Task<int> BackfillAsync(int batchSize, CancellationToken cancellationToken);
}

using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Failures;

/// <summary>
/// Which provider failure, if any, a run is recorded as having ended on. ONE definition of that answer, asked by the run page's
/// remedy and by the room's diagnostic, so the card and the code beside it can never name different failures.
/// </summary>
public interface IRunFailureReader
{
    /// <summary>
    /// The failure the run ended on, or null: it recorded none, it is not the team's, or it did not fail on a provider connection.
    /// For a failed run the node's or run's own record wins and the last agent run's result is the fallback; for a run that did
    /// not fail, only a push the remote refused counts — the run stayed whole, and its branch was not published. The result names the node that ended the
    /// run (<see cref="RecordedFailure.NodeId"/>), which is how a parent says where in a child run the failure happened.
    /// </summary>
    Task<RecordedFailure?> ReadAsync(Guid runId, Guid teamId, CancellationToken cancellationToken);
}

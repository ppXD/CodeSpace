using CodeSpace.Messages.Failures;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Thrown when a rehydrate needs a settled node's ORIGINAL outputs, the row admits it holds only the redaction
/// stand-in, and the encrypted same-record sidecar that was supposed to hold the originals is not there. The
/// values existed only in the process that ran the node; nothing on disk can reproduce them.
///
/// <para>Why this FAILS the run rather than parking it for a human: the node is already recorded settled, and a
/// settled node is never re-dispatched on this run (<c>RehydrateFromLedgerAsync</c> re-runs only nodes with no
/// terminal record), so there is no action — human or automatic — that recovers the value here. A park would be a
/// wait nobody can resolve. The operator's real remedy is a from-node RERUN, which forks a new run that OMITS
/// this cell and re-executes it; the failure message names the node so they know where to start it.</para>
///
/// <para>Separate from <c>NodeFailureException</c> deliberately, for the reason <c>WorkflowSecretLeakException</c>
/// is: a node's error edge must not swallow this. An error edge would route the run onward as if a node had
/// merely failed, which is the same "keep going on something that isn't the value" this guard exists to stop.</para>
/// </summary>
public sealed class WorkflowRedactedOutputsUnrecoverableException : Exception, IFailure
{
    /// <summary>Internal, not Unprocessable: nothing the caller sent caused this. The engine promised a redacted row's originals would stay recoverable and they are not — an invariant of ours that did not hold, and always worth waking someone.</summary>
    public FailureKind Kind => FailureKind.Internal;

    public string Code => FailureCodes.WorkflowOutputsUnrecoverable;

    public WorkflowRedactedOutputsUnrecoverableException(string cell) : base(
        $"Cell '{cell}' recorded outputs that were redacted for persistence, and its encrypted recovery payload is not readable — the original values are unrecoverable. " +
        "The run cannot resume on the redaction placeholder; re-run the workflow from this node to reproduce them.")
    { }
}

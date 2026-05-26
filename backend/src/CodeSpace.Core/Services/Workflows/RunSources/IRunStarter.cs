namespace CodeSpace.Core.Services.Workflows.RunSources;

/// <summary>
/// Single source-of-truth for "given an envelope, create the run". Stages the
/// <c>WorkflowRunRequest</c> + <c>WorkflowRun</c> EF entities on the caller's
/// <c>CodeSpaceDbContext</c> AND emits the <c>run.queued</c> ledger record. Caller is
/// responsible for calling <c>SaveChangesAsync</c> — keeping the boundary at the caller lets
/// it batch the staging with other writes in the same transaction (the dispatcher, for
/// example, processes a batch of webhook matches inside one transaction).
///
/// <para>Returns the new <c>workflow_run.id</c> the caller can hand back to the operator
/// / pass to the engine.</para>
/// </summary>
public interface IRunStarter
{
    /// <summary>
    /// Stage the (request + run) pair on the active <c>DbContext</c> change tracker and emit
    /// the <c>run.queued</c> lifecycle record. Returns the run id. Throws
    /// <see cref="ArgumentException"/> for malformed envelopes (e.g. ActorType=User without
    /// ActorId, ActorType=Webhook with a non-null ActorId).
    /// </summary>
    Task<Guid> StartAsync(RunSourceEnvelope envelope, CancellationToken cancellationToken);
}

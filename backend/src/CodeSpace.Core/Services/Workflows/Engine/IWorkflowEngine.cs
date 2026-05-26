namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// The Hangfire-invoked worker that executes a workflow. Also reachable directly from
/// integration tests via <c>IWorkflowEngine</c> resolution (see InMemoryBackgroundJobClient).
/// One method — give me a run id, I'll walk its DAG to completion, persist every node
/// result, set the run's final status. Idempotent: re-calling on an already-completed run
/// is a no-op; re-calling on a Pending run picks up where it left off.
/// </summary>
public interface IWorkflowEngine
{
    Task ExecuteRunAsync(Guid runId, CancellationToken cancellationToken);
}

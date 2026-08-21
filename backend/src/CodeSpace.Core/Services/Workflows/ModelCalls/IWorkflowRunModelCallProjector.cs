using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

/// <summary>
/// Bounded, idempotent shadow projection from the append-only interaction tape into the first-class Workflow Run
/// model-call plane. Source identities, not a BIGSERIAL watermark, define admission and late-evidence revisits.
/// </summary>
public interface IWorkflowRunModelCallProjector : IScopedDependency
{
    Task<WorkflowRunModelCallProjectionResult> SweepAsync(int batchSize, CancellationToken cancellationToken);
}

public sealed record WorkflowRunModelCallProjectionResult(int TerminalAttemptsProjected, int LateStartsAttached, int BodyCapturesDeclared = 0)
{
    public int TotalChanges => TerminalAttemptsProjected + LateStartsAttached + BodyCapturesDeclared;
}

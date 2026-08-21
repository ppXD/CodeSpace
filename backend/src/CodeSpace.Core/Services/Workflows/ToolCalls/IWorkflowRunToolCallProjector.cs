using CodeSpace.Core.DependencyInjection;

namespace CodeSpace.Core.Services.Workflows.ToolCalls;

/// <summary>
/// Bounded, idempotent observation-only projection of workflow-bound governed side effects. ToolCallLedger remains
/// the approval, execution, exactly-once and replay authority; this contract only appends queryable 0141 facts.
/// </summary>
public interface IWorkflowRunToolCallProjector : IScopedDependency
{
    Task<WorkflowRunToolCallProjectionResult> SweepAsync(int batchSize, CancellationToken cancellationToken);
}

/// <summary>
/// Projection changes plus one bounded diagnostic sample. Every Observed value describes this sweep's stable
/// primary-key sample, not a cumulative unique count; excluded source rows intentionally have no projection cursor.
/// </summary>
public sealed record WorkflowRunToolCallProjectionResult
{
    public int CallsProjected { get; init; }
    public int DiagnosticRowsObserved { get; init; }
    public int LegacyUnorderedSourcesObserved { get; init; }
    public int DecisionSourcesObserved { get; init; }
    public int StandaloneSourcesObserved { get; init; }
    public int InvalidScopeSourcesObserved { get; init; }
    public int DeferredLiveSourcesObserved { get; init; }
    public int InvalidSourceFactsObserved { get; init; }
}

using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Constants;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.Engine;

/// <summary>
/// Spawns a child workflow run for a <c>flow.subworkflow</c> node and dispatches it. Staging
/// (create the row) and dispatch are SEPARATE on purpose: the engine commits the parent's
/// <c>Subworkflow</c> wait + flips the parent to <c>Suspended</c> BEFORE the child is dispatched,
/// so a fast child can't finish and try to resume the parent before the parent is actually parked.
/// </summary>
public interface ISubworkflowService
{
    /// <summary>
    /// Create (but do NOT dispatch) a child run of <paramref name="childWorkflowId"/> with
    /// <paramref name="inputsJson"/> as its normalized payload, linked to <paramref name="parent"/>
    /// via <c>parent_run_id</c>. Returns the new child run id. Throws
    /// <see cref="SubworkflowStartException"/> when the child can't be started (not found / not in
    /// the parent's team / no published version / nesting too deep).
    /// </summary>
    Task<Guid> StageChildRunAsync(WorkflowRun parent, Guid childWorkflowId, int? version, string inputsJson, CancellationToken cancellationToken);

    /// <summary>Dispatch a previously-staged child run (Pending → Enqueued → engine). Called once the parent is committed Suspended.</summary>
    Task DispatchChildRunAsync(Guid childRunId, CancellationToken cancellationToken);
}

public sealed class SubworkflowService : ISubworkflowService, IScopedDependency
{
    /// <summary>Hard cap on nested sub-workflow depth — a runaway guard against direct/indirect recursion (A→A, A→B→A).</summary>
    public const int MaxDepth = 8;

    private readonly CodeSpaceDbContext _db;
    private readonly IRunStarter _runStarter;
    private readonly IWorkflowRunDispatcher _dispatcher;
    private readonly ILogger<SubworkflowService> _logger;

    public SubworkflowService(CodeSpaceDbContext db, IRunStarter runStarter, IWorkflowRunDispatcher dispatcher, ILogger<SubworkflowService> logger)
    {
        _db = db;
        _runStarter = runStarter;
        _dispatcher = dispatcher;
        _logger = logger;
    }

    public async Task<Guid> StageChildRunAsync(WorkflowRun parent, Guid childWorkflowId, int? version, string inputsJson, CancellationToken cancellationToken)
    {
        var child = await _db.Workflow.AsNoTracking()
            .Where(w => w.Id == childWorkflowId && w.TeamId == parent.TeamId && w.DeletedDate == null)
            .Select(w => new { w.Id, w.Name, w.LatestVersion, w.Enabled })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new SubworkflowStartException($"Sub-workflow {childWorkflowId} not found in this team.");

        var targetVersion = version ?? child.LatestVersion;
        if (targetVersion <= 0)
            throw new SubworkflowStartException($"Sub-workflow '{child.Name}' has no published version to run.");

        await EnsureWithinDepthAsync(parent.Id, cancellationToken).ConfigureAwait(false);

        var childRunId = await _runStarter.StartAsync(new RunSourceEnvelope
        {
            TeamId = parent.TeamId,
            WorkflowId = childWorkflowId,
            WorkflowVersion = targetVersion,
            SourceType = WorkflowRunSourceTypes.ChildWorkflow,
            ActorType = WorkflowRunActorTypes.System,
            // The originating actor travels down the chain so the child's audit trail attributes
            // back to the human who started the root run (System actor + the root user's id).
            ActorId = parent.CreatedBy == Guid.Empty ? null : parent.CreatedBy,
            NormalizedPayloadJson = string.IsNullOrWhiteSpace(inputsJson) ? "{}" : inputsJson,
            ParentRunId = parent.Id,
            CreatedBy = parent.CreatedBy,
        }, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Sub-workflow child staged. ParentRunId={ParentRunId} ChildWorkflowId={ChildWorkflowId} ChildRunId={ChildRunId} Version={Version}",
            parent.Id, childWorkflowId, childRunId, targetVersion);

        return childRunId;
    }

    public async Task DispatchChildRunAsync(Guid childRunId, CancellationToken cancellationToken) =>
        await _dispatcher.DispatchAsync(childRunId, cancellationToken).ConfigureAwait(false);

    /// <summary>Cycle guard for the ancestry walk — a parent-run chain (real nesting + rerun lineage) never approaches this; a corrupt cycle can't loop forever.</summary>
    private const int MaxChainWalk = 512;

    /// <summary>
    /// Walk the <c>parent_run_id</c> chain up from <paramref name="parentRunId"/> and refuse once the child we're about
    /// to create would exceed <see cref="MaxDepth"/> subworkflow-nesting levels. A hop is counted as NESTING only when
    /// the run it steps FROM is a genuine nesting run — a from-node RERUN / REPLAY fork links via <c>parent_run_id</c>
    /// purely as rerun LINEAGE (D2 made <c>flow.subworkflow</c> rerunnable, routing such links into this walk), so those
    /// hops must NOT inflate the depth, else N successive reruns of a subworkflow would spuriously trip this guard on
    /// zero real nesting. The walk is bounded by <see cref="MaxChainWalk"/> against a corrupt cycle.
    /// </summary>
    private async Task EnsureWithinDepthAsync(Guid parentRunId, CancellationToken cancellationToken)
    {
        var nestingDepth = 0;
        Guid? cursor = parentRunId;

        for (var hop = 0; cursor is { } id && hop < MaxChainWalk; hop++)
        {
            var run = await _db.WorkflowRun.AsNoTracking()
                .Where(r => r.Id == id)
                .Select(r => new { r.ParentRunId, r.SourceType })
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (run is null) break;

            var isRerunLineageHop = run.SourceType is WorkflowRunSourceTypes.Rerun or WorkflowRunSourceTypes.Replay;
            cursor = run.ParentRunId;

            if (cursor.HasValue && !isRerunLineageHop && ++nestingDepth + 1 >= MaxDepth)
                throw new SubworkflowStartException($"Sub-workflow nesting exceeds the limit of {MaxDepth} levels (possible recursion).");
        }
    }
}

/// <summary>A child run could not be started (missing / cross-team / unpublished / too deep). The node surfaces this as a clean node failure.</summary>
public sealed class SubworkflowStartException : Exception
{
    public SubworkflowStartException(string message) : base(message) { }
}

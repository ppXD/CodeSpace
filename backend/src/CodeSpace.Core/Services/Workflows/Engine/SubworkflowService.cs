using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Dispatch;
using CodeSpace.Core.Services.Workflows.Failures;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Failures;
using CodeSpace.Messages.Workflows;
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
    /// <paramref name="inputs"/> as its normalized payload — the public copy in the request's payload column, the
    /// sealed exact copy (when a parent secret was masked) beside it — linked to <paramref name="parent"/>
    /// via <c>parent_run_id</c>. Returns the new child run id. Throws
    /// <see cref="SubworkflowStartException"/> when the child can't be started (not found / not in
    /// the parent's team / no published version / nesting too deep).
    /// </summary>
    Task<Guid> StageChildRunAsync(WorkflowRun parent, Guid childWorkflowId, int? version, SealedPayload inputs, CancellationToken cancellationToken);

    /// <summary>Dispatch a previously-staged child run (Pending → Enqueued → engine). Called once the parent is committed Suspended.</summary>
    Task DispatchChildRunAsync(Guid childRunId, CancellationToken cancellationToken);

    /// <summary>
    /// When a child run reaches a terminal state, resume the parent parked on it: find the parent's pending
    /// <c>Subworkflow</c> wait whose <c>Token</c> equals this child's id (which also distinguishes a sub-workflow child
    /// from a replay child — a replay's "parent" holds no such wait), then resolve it with the child's status + outputs +
    /// error — and, for a child that ended on a provider failure a person can repair, that failure and the run it happened in — so the
    /// node can map them, via the wait-for-all completion path. Returns true iff THIS call resolved the wait
    /// (false when there is no such pending wait — not a sub-workflow child, or a concurrent resume / this child's own
    /// happy-path resume already won). Idempotent (the wait CAS) and best-effort (a failure is logged, never propagated —
    /// the child already completed cleanly). The ONE resume path shared by the engine's on-completion call AND the
    /// reconciler's stranded-parent backstop, so the child-output mapping can never drift between them.
    /// </summary>
    Task<bool> ResumeParentIfChildTerminalAsync(WorkflowRun child, WorkflowRunStatus status, string? error, CancellationToken cancellationToken);
}

public sealed class SubworkflowService : ISubworkflowService, IScopedDependency
{
    /// <summary>Hard cap on nested sub-workflow depth — a runaway guard against direct/indirect recursion (A→A, A→B→A).</summary>
    public const int MaxDepth = 8;

    private readonly CodeSpaceDbContext _db;
    private readonly IRunStarter _runStarter;
    private readonly IWorkflowRunDispatcher _dispatcher;
    private readonly IWorkflowResumeService _resumeService;
    private readonly IRunFailureReader _failures;
    private readonly ILogger<SubworkflowService> _logger;

    public SubworkflowService(CodeSpaceDbContext db, IRunStarter runStarter, IWorkflowRunDispatcher dispatcher, IWorkflowResumeService resumeService, IRunFailureReader failures, ILogger<SubworkflowService> logger)
    {
        _db = db;
        _runStarter = runStarter;
        _dispatcher = dispatcher;
        _resumeService = resumeService;
        _failures = failures;
        _logger = logger;
    }

    public async Task<Guid> StageChildRunAsync(WorkflowRun parent, Guid childWorkflowId, int? version, SealedPayload inputs, CancellationToken cancellationToken)
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
            NormalizedPayloadJson = string.IsNullOrWhiteSpace(inputs.PublicJson) ? "{}" : inputs.PublicJson,
            SealedPayloadCiphertext = inputs.Ciphertext,
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

    public async Task<bool> ResumeParentIfChildTerminalAsync(WorkflowRun child, WorkflowRunStatus status, string? error, CancellationToken cancellationToken)
    {
        if (child.ParentRunId == null) return false;

        try
        {
            var childIdToken = child.Id.ToString();
            var waitId = await _db.WorkflowRunWait.AsNoTracking()
                .Where(w => w.RunId == child.ParentRunId && w.WaitKind == WorkflowWaitKinds.Subworkflow
                            && w.Token == childIdToken && w.Status == WorkflowWaitStatuses.Pending)
                .Select(w => (Guid?)w.Id)
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (waitId is null) return false;   // not a sub-workflow child (e.g. a replay), or its wait already resolved

            var failure = status == WorkflowRunStatus.Failure ? await ReadChildFailureAsync(child, cancellationToken).ConfigureAwait(false) : null;
            var payload = BuildResumePayload(child, status, error, failure);

            // Resolve ONLY this child's own wait — never the sibling waits a parallel fan-out of children holds — so each
            // parent subworkflow node resumes with its own child's outputs. The wait-COMPLETION path's CAS makes a
            // re-fire (a reconciler backstop racing this child's own happy-path resume) an idempotent no-op.
            return await _resumeService.ResumeOnWaitCompletionAsync(child.ParentRunId.Value, waitId.Value, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: the child already completed cleanly, so a resume failure is logged for the reconciler to
            // retry, never propagated back into the child's terminal path.
            _logger.LogError(ex, "Sub-workflow child {ChildRunId} is terminal but resuming parent {ParentRunId} failed", child.Id, child.ParentRunId);
            return false;
        }
    }

    /// <summary>
    /// The provider failure the child's run ended on, read by the reader the child's own run page asks (so the parent can never be told a different
    /// failure than the child shows), with the run it happened in. A failure the child itself carried up from a child of its own keeps the origin it
    /// already has — run, node and the time that run recorded it — so one that passed through nested sub-workflows names the deepest run that met it, and
    /// is judged from when it did. Null when the child ended on anything else, and
    /// when the record cannot be read: resuming the parent never waits on it, and a parent resumed without it fails with the sentence it always had.
    /// </summary>
    private async Task<PersistedFailure?> ReadChildFailureAsync(WorkflowRun child, CancellationToken cancellationToken)
    {
        try
        {
            var recorded = await _failures.ReadAsync(child.Id, child.TeamId, cancellationToken).ConfigureAwait(false);

            return recorded is null ? null : recorded.Failure with { Origin = recorded.Failure.Origin ?? new FailureOrigin { RunId = child.Id, NodeId = recorded.NodeId, At = recorded.At } };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not read the failure sub-workflow child {ChildRunId} ended on; its parent resumes with the sentence alone", child.Id);

            return null;
        }
    }

    /// <summary>The resume payload the sub-workflow node maps: status, outputs and error as it always carried, plus the child's failure under the key a ledger record keeps one under — absent unless there is one, so a payload without a failure is byte-identical to what it was.</summary>
    private static string BuildResumePayload(WorkflowRun child, WorkflowRunStatus status, string? error, PersistedFailure? failure)
    {
        var payload = new Dictionary<string, object?> { ["status"] = status.ToString(), ["outputs"] = JsonSafeParseObject(child.OutputsJson), ["error"] = error };

        if (failure is not null) payload[PersistedFailureJson.PayloadKey] = PersistedFailureJson.ToElement(failure);

        return JsonSerializer.Serialize(payload);
    }

    /// <summary>Parse a run's OutputsJson into a JsonElement object for the resume payload; an empty object on any trouble.</summary>
    private static JsonElement JsonSafeParseObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return JsonDocument.Parse("{}").RootElement.Clone();

        try
        {
            var root = JsonDocument.Parse(json).RootElement;
            return root.ValueKind == JsonValueKind.Object ? root.Clone() : JsonDocument.Parse("{}").RootElement.Clone();
        }
        catch
        {
            return JsonDocument.Parse("{}").RootElement.Clone();
        }
    }

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

using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The half of the plane that writes a harness's OWN model calls into the model-call plane 0124/0130 defines — the same
/// two tables a workflow LLM node's calls land in, so "which model did what, at what cost" has one answer shape whether
/// the call went through <c>ILLMClient</c> or was made by a CLI inside itself.
///
/// <para><b>Why it rides the frames' own transaction.</b> A call row's only evidence is the frame it was read out of, so
/// the two become durable together or not at all. The price is the one the reduction checkpoint already pays: a refused
/// call row takes that batch of frames down with it, contained exactly as a refused batch already is — capture stops for
/// the round and the run is untouched.</para>
///
/// <para><b>Two queries per batch, and only for a batch that carries calls.</b> A harness that prints no per-call record
/// contributes none, so the cost of this path for Codex is one integer comparison. There is no per-frame round trip: the
/// scope and the already-admitted keys are read once for the whole batch, which is the same reason the batch exists.</para>
///
/// <para><b>What it will not write.</b> Nothing at all when the Agent Run belongs to no workflow run — the model-call
/// plane is keyed to a workflow run and refuses a row that names none, so a standalone agent run's internal calls are
/// simply not projected rather than attached to an invented parent. And nothing for a key the plane has already admitted,
/// which is what makes re-projecting the same frames a no-op.</para>
/// </summary>
public sealed partial class NativeRecordPlane
{
    /// <summary>The attempt status a harness's own response record supports: the harness received the response it is describing. A retry the CLI never printed is not observable here, so no other status is reachable from this source.</summary>
    private const string ObservedAttemptStatus = "Succeeded";

    /// <summary>
    /// Stage the batch's model calls onto the batch's own unit of work. Skips a projection whose source identity the
    /// plane has already admitted, and collapses two frames of one response inside a single batch, so the unique source
    /// identity is the backstop under any writer rather than the thing this path relies on to be correct.
    /// </summary>
    private async Task StageModelCallsAsync(CodeSpaceDbContext db, NativeRecordBatch batch, CancellationToken cancellationToken)
    {
        if (batch.ModelCalls.Count == 0) return;

        if (await ModelCallScopeAsync(db, batch.Handle, cancellationToken).ConfigureAwait(false) is not { } scope) return;

        var projections = batch.ModelCalls.DistinctBy(projection => projection.SourceCorrelationId).ToList();
        var admitted = await AdmittedCorrelationsAsync(db, scope, projections, cancellationToken).ConfigureAwait(false);

        foreach (var projection in projections.Where(candidate => !admitted.Contains(candidate.SourceCorrelationId)))
        {
            db.WorkflowRunModelCall.Add(CallRow(scope, projection));
            db.WorkflowRunModelCallAttempt.Add(AttemptRow(scope, projection));
        }
    }

    /// <summary>
    /// The workflow-run scope a harness's calls belong to, read off the Agent Run itself — the authority on which
    /// workflow run, node and cell it is executing for. Null when the run names no workflow run, which is not a failure:
    /// a standalone agent run's calls have no place in a run-keyed plane, and the log says so once per batch rather than
    /// once per call.
    /// </summary>
    private async Task<ModelCallScope?> ModelCallScopeAsync(CodeSpaceDbContext db, NativeRecordCaptureHandle handle, CancellationToken cancellationToken)
    {
        var scope = await db.AgentRun.AsNoTracking()
            .Where(run => run.TeamId == handle.TeamId && run.Id == handle.AgentRunId && run.WorkflowRunId != null)
            .Select(run => new ModelCallScope(run.TeamId, run.WorkflowRunId!.Value, run.NodeId, run.IterationKey))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (scope is null)
            _logger.LogInformation("Agent run {RunId} belongs to no workflow run, so the model calls its harness recorded are not projected into the run-keyed model-call plane; its frames and the run itself are unaffected", handle.AgentRunId);

        return scope;
    }

    /// <summary>The source identities this run has already admitted, among the ones this batch carries — one query for the batch, so a re-projected frame costs no more than a new one.</summary>
    private static async Task<HashSet<Guid>> AdmittedCorrelationsAsync(CodeSpaceDbContext db, ModelCallScope scope, IReadOnlyList<HarnessModelCallProjectionV1> projections, CancellationToken cancellationToken)
    {
        var correlations = projections.Select(projection => projection.SourceCorrelationId).ToArray();

        var admitted = await db.WorkflowRunModelCall.AsNoTracking()
            .Where(call => call.TeamId == scope.TeamId && call.WorkflowRunId == scope.WorkflowRunId
                && call.SourceCorrelationId != null && correlations.Contains(call.SourceCorrelationId.Value))
            .Select(call => call.SourceCorrelationId!.Value)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        return admitted.ToHashSet();
    }

    /// <summary>
    /// The logical call. The requested route stays NULL because a harness's response record states what was SERVED and
    /// never what was asked for, and the execution-identity triple stays NULL because the grounded semantic event
    /// projected from the same frame is what actually binds the call to its harness execution.
    /// </summary>
    private static WorkflowRunModelCall CallRow(ModelCallScope scope, HarnessModelCallProjectionV1 projection) => new()
    {
        Id = projection.ModelCallId, TeamId = scope.TeamId, WorkflowRunId = scope.WorkflowRunId,
        NodeId = scope.NodeId, IterationKey = scope.IterationKey, CallOrdinal = projection.CallOrdinal,
        Purpose = projection.Purpose, SourceKind = projection.SourceKind, SourceCorrelationId = projection.SourceCorrelationId,
        CaptureSource = projection.SourceKind, CaptureCompleteness = projection.Completeness,
        SchemaVersion = projection.ContractVersion,
        CreatedDate = projection.ObservedAt, LastModifiedDate = projection.ObservedAt,
    };

    /// <summary>
    /// The one physical attempt the record evidences. Every figure the record did not state is left NULL and NAMED in
    /// <see cref="WorkflowRunModelCallAttempt.UnavailableFigures"/>, so no reader can read an absence as a measured zero;
    /// <c>started_at</c> carries the ingest instant because the column admits no absence, which is why the timing pair
    /// beside it is declared unavailable instead of repeating that instant and claiming a call of zero duration.
    /// </summary>
    private static WorkflowRunModelCallAttempt AttemptRow(ModelCallScope scope, HarnessModelCallProjectionV1 projection) => new()
    {
        Id = projection.AttemptId, TeamId = scope.TeamId, WorkflowRunId = scope.WorkflowRunId,
        ModelCallId = projection.ModelCallId, AttemptOrdinal = 1, EffectiveModel = projection.Model,
        TransportKind = projection.TransportKind, Status = ObservedAttemptStatus, FinishReason = projection.FinishReason,
        CaptureSource = projection.SourceKind, CaptureCompleteness = projection.Completeness,
        InputTokens = projection.InputTokens, OutputTokens = projection.OutputTokens,
        CacheReadTokens = projection.CacheReadTokens, CacheWriteTokens = projection.CacheWriteTokens,
        CostAmount = projection.CostAmount, CostCurrency = projection.CostCurrency, PricingVersion = projection.PricingVersion,
        UnavailableFigures = projection.UnavailableFigures.ToArray(),
        SourceNativeRecordId = projection.SourceNativeRecordId,
        StartedAt = projection.ObservedAt, SchemaVersion = projection.ContractVersion,
        CreatedDate = projection.ObservedAt, LastModifiedDate = projection.ObservedAt,
    };

    /// <summary>Which workflow run, node and cell a harness's calls belong to. Read from the Agent Run rather than accepted from a caller that could disagree with it, exactly as the capture opening reads its run scope.</summary>
    private sealed record ModelCallScope(Guid TeamId, Guid WorkflowRunId, string? NodeId, string IterationKey);
}

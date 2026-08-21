using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.RunData;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Agents.Capture;

/// <summary>
/// The durable floor of the harness data plane: every native frame a harness produced lands as its own row, and the
/// normalized events a harness parser yields land beside it as PROJECTIONS that cite the exact frames they came from.
///
/// <para>Why it exists. The normalized <c>agent_run_event</c> log is the only durable interpretation of a harness's
/// output today, and it is lossy by construction — <see cref="IAgentHarness.ParseEvents"/> returns an empty list for
/// any line it does not recognise, so a native frame class the adapter never learned leaves no row at all, while the
/// raw bytes survive only in a spool that ages out. Every remaining defect in this area is a symptom of that: a Codex
/// run carries no per-call model or cost because those facts were never captured as first-class records, and a
/// re-attach can only fold a tail because there is no durable record stream to fold a prefix from.</para>
///
/// <para>This is a DUAL WRITE. Nothing reads the record and event tables yet, the normalized log and the run's result
/// keep their present semantics, and no failure here may change what an Agent Run resolves to. That is a property of
/// the wiring, not a promise: capture runs on its OWN unit of work (see <see cref="NativeRecordPlane"/>), so a refused
/// write cannot strand rows in the tracker the run's next save replays, and the pump above it contains every
/// failure.</para>
///
/// <para><b>One thing it writes IS read, and it is telemetry.</b> A batch also carries the model calls a harness's own
/// records state, and those land in the model-call plane, whose rows the id-addressed model-call reader surfaces
/// alongside a workflow node's calls — that is the point of projecting them. What they remain outside of is every
/// authority: nothing here is read by completion, terminal decision, planner, oracle, critic or model routing, and the
/// derived per-run token aggregate on <c>AgentRunResult</c> is computed exactly as it was and is unaffected by whether
/// a single call row was written.</para>
///
/// <para><b>What this interface does and does not own.</b> It owns ONE live opening: mint or re-enter the execution,
/// write batches, record how the round's process ended. The two things an opening cannot see — resuming the process a
/// replaced worker was observing, and closing the execution once the executor reaches a terminal — are
/// <see cref="INativeRecordExecutionPlane"/>, and folding the captured prefix into a resumable reduction is
/// <see cref="INativeRecordReductionPlane"/>. Both are SIBLING interfaces (Rule 7) that
/// <see cref="NativeRecordPlane"/> also implements, so a caller feature-detects them and a caller that does not need
/// them keeps exactly this contract.</para>
/// </summary>
public interface INativeRecordPlane
{
    /// <summary>
    /// Open a capture stream against a durable harness execution and the physical process inside it, minting the
    /// execution when the Agent Run has no live one and re-entering the live one when it has (a revise round is the
    /// next PROCESS of the same execution, not a new one). Null ⇒ the plane could not open; capture is skipped for
    /// this round and the run proceeds untouched.
    /// </summary>
    Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken);

    /// <summary>Persist one batch of captured frames, the events projected from them and the model calls those frames record, in ONE transaction — so a projection can never be durable while the frame it cites is not.</summary>
    Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken);

    /// <summary>Record how this round's physical process ended: <see cref="HarnessProcessAttemptState.Exited"/> with the code when it is known, <see cref="HarnessProcessAttemptState.Lost"/> with a reason when it is not.</summary>
    Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken);
}

/// <summary>
/// A capture the data contract refuses. An invariant this system is supposed to hold did not hold, so it is
/// <see cref="FailureKind.Internal"/> and carries the masked code — no caller ever sees it: the pump contains it,
/// capture stops for the round, and the run is untouched, which is the only reason validating here is safe at all.
/// </summary>
public sealed class NativeRecordContractException : Exception, IFailure
{
    public FailureKind Kind => FailureKind.Internal;

    public string Code => FailureCodes.Internal;

    public NativeRecordContractException(string message) : base(message) { }
}

/// <summary>
/// The plane's writer. Every operation runs in its OWN DI scope, so its DbContext is never the Agent Run's: a refused
/// write disposes its staged rows with that scope instead of stranding them Added in the tracker the run's very next
/// save replays, and a long run's frames cannot grow a tracker the run is still using. That is what makes "no failure
/// here may change what an Agent Run resolves to" a property of the wiring rather than a promise — and it is load
/// bearing, because the refusals are reachable BY DESIGN: 0137 rejects a superseded worker's fence on exactly the
/// reclaim-for-reattach case, which is the outcome that case is supposed to have.
///
/// <para>It also ACCOUNTS for what it wrote, for TWO facets of the run's record. Every batch advances the completeness
/// statement for the <see cref="WorkflowRunDataOwnerKinds.NativeRecord"/> facet, and a batch the database refuses
/// becomes a known-missing span; every LAUNCH does the same for
/// <see cref="WorkflowRunDataOwnerKinds.HarnessProcessAttempt"/>, whose expectation — one process record per launch — is
/// the first in this plane that is declared rather than discovered. Each half lives in its own completeness partial,
/// which states plainly what a complete verdict there does and does not mean, and that nothing reads either table
/// yet.</para>
/// </summary>
public sealed partial class NativeRecordPlane : INativeRecordPlane, IScopedDependency
{
    /// <summary>Reason stamped on an attempt whose observer never saw an exit code — a forced terminal (timeout, stall) or a worker torn down mid-round.</summary>
    public const string ProcessOutcomeUnobservedErrorCode = "capture.exit-unobserved";

    /// <summary>How many contract errors a refusal names before it stops listing. A writer bug repeats per frame; five is enough to diagnose it and short enough to log.</summary>
    private const int ReportedContractErrors = 5;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IRunDataCompletenessWriter _completeness;
    private readonly ILogger<NativeRecordPlane> _logger;

    public NativeRecordPlane(IServiceScopeFactory scopeFactory, IRunDataCompletenessWriter completeness, ILogger<NativeRecordPlane> logger)
    {
        _scopeFactory = scopeFactory;
        _completeness = completeness;
        _logger = logger;
    }

    public async Task<NativeRecordCaptureHandle?> OpenAsync(NativeRecordCaptureRequest request, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var run = await LoadRunScopeAsync(db, request, cancellationToken).ConfigureAwait(false);

        if (run is null) return null;

        var execution = await ResolveExecutionAsync(db, request, run, cancellationToken).ConfigureAwait(false);
        var attempt = AppendedAttempt(request, execution);

        db.WorkflowRunHarnessProcessAttempt.Add(attempt);
        await AppendAttemptAsync(db, request, run.WorkflowRunId, attempt.Id, cancellationToken).ConfigureAwait(false);

        return new NativeRecordCaptureHandle
        {
            TeamId = request.TeamId, AgentRunId = request.AgentRunId, ExecutionId = execution.Id,
            AttemptId = attempt.Id, StreamId = Guid.NewGuid(), Channel = request.Channel,
            WorkflowRunId = run.WorkflowRunId,
        };
    }

    public async Task WriteAsync(NativeRecordBatch batch, CancellationToken cancellationToken)
    {
        if (batch.Records.Count == 0 && batch.Events.Count == 0 && batch.ModelCalls.Count == 0) return;

        EnsureContractual(batch);

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        foreach (var capture in batch.Records) db.WorkflowRunNativeRecord.Add(RecordRow(batch.Handle, capture));
        foreach (var projection in batch.Events) db.WorkflowRunSemanticEvent.Add(EventRow(batch.Handle, projection));

        await StageModelCallsAsync(db, batch, cancellationToken).ConfigureAwait(false);

        await CommitAsync(db, batch, cancellationToken).ConfigureAwait(false);
    }

    public async Task CloseAsync(NativeRecordCaptureHandle handle, int? exitCode, CancellationToken cancellationToken)
    {
        var closedAt = DateTimeOffset.UtcNow;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        // A pure guarded UPDATE rather than a tracked save: the attempt's own AFTER-insert trigger already advanced
        // its parent execution behind EF's cached xmin, so a tracked round trip would fight a row it did not write.
        await db.WorkflowRunHarnessProcessAttempt
            .Where(attempt => attempt.TeamId == handle.TeamId && attempt.Id == handle.AttemptId && attempt.State == HarnessProcessAttemptState.Running)
            .ExecuteUpdateAsync(set => set
                .SetProperty(attempt => attempt.State, exitCode is null ? HarnessProcessAttemptState.Lost : HarnessProcessAttemptState.Exited)
                .SetProperty(attempt => attempt.ExitCode, exitCode)
                .SetProperty(attempt => attempt.ErrorCode, exitCode is null ? ProcessOutcomeUnobservedErrorCode : null)
                .SetProperty(attempt => attempt.ErrorMessage, exitCode is null ? "The observer recorded no exit code for this harness process, so its outcome is unknown rather than assumed." : null)
                .SetProperty(attempt => attempt.ExitedAt, closedAt)
                .SetProperty(attempt => attempt.LastObservedAt, closedAt)
                .SetProperty(attempt => attempt.LastModifiedAt, closedAt)
                .SetProperty(attempt => attempt.Revision, attempt => attempt.Revision + 1), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>The Agent Run's own tenant-bound scope. The execution guard proves <c>workflow_run_id</c> EQUALS this value, so it is read from the row rather than accepted from a caller that could disagree with it.</summary>
    private async Task<RunScope?> LoadRunScopeAsync(CodeSpaceDbContext db, NativeRecordCaptureRequest request, CancellationToken cancellationToken)
    {
        var runScope = await db.AgentRun.AsNoTracking()
            .Where(run => run.TeamId == request.TeamId && run.Id == request.AgentRunId)
            .Select(run => new RunScope(run.WorkflowRunId))
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (runScope is null)
            _logger.LogWarning("Native record capture found no tenant-bound agent run {RunId} for team {TeamId}; capture is skipped and the run proceeds unchanged", request.AgentRunId, request.TeamId);

        return runScope;
    }

    /// <summary>
    /// The live execution of this Agent Run, or a fresh next generation when it has none. Re-entering a live one is
    /// what makes a revise round the next PROCESS rather than a new execution. The row it leaves live is closed by
    /// <see cref="INativeRecordExecutionPlane.TerminalizeAsync"/> when the executor reaches a terminal, which is what
    /// keeps the generation gate — a new generation cannot open over a live predecessor — from wedging the next run.
    /// </summary>
    private static async Task<WorkflowRunHarnessExecution> ResolveExecutionAsync(CodeSpaceDbContext db, NativeRecordCaptureRequest request, RunScope run, CancellationToken cancellationToken)
    {
        // NOT tracked, deliberately: the head this reads is advanced by the appended attempt's own database trigger,
        // so a tracked entity would hand the NEXT round of the same run the ordinal that round already used.
        var live = await db.WorkflowRunHarnessExecution.AsNoTracking()
            .Where(execution => execution.TeamId == request.TeamId && execution.AgentRunId == request.AgentRunId
                && (execution.State == HarnessExecutionState.Pending || execution.State == HarnessExecutionState.Running))
            .OrderByDescending(execution => execution.Generation)
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);

        if (live is not null) return live;

        var opened = OpenedExecution(request, run, await NextGenerationAsync(db, request, cancellationToken).ConfigureAwait(false));

        db.WorkflowRunHarnessExecution.Add(opened);

        return opened;
    }

    private static async Task<int> NextGenerationAsync(CodeSpaceDbContext db, NativeRecordCaptureRequest request, CancellationToken cancellationToken)
    {
        var highest = await db.WorkflowRunHarnessExecution.AsNoTracking()
            .Where(execution => execution.TeamId == request.TeamId && execution.AgentRunId == request.AgentRunId)
            .MaxAsync(execution => (int?)execution.Generation, cancellationToken).ConfigureAwait(false);

        return (highest ?? 0) + 1;
    }

    private static WorkflowRunHarnessExecution OpenedExecution(NativeRecordCaptureRequest request, RunScope run, int generation)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunHarnessExecution
        {
            Id = Guid.NewGuid(), TeamId = request.TeamId, AgentRunId = request.AgentRunId, WorkflowRunId = run.WorkflowRunId,
            Generation = generation, HarnessTypeKey = request.HarnessTypeKey, RunnerKind = request.RunnerKind,
            RunnerLocatorSchemaVersion = 1, State = HarnessExecutionState.Pending, AttemptCount = 0,
            NextAttemptOrdinal = 1, LeaseFence = 0, Revision = 1, CreatedAt = now, LastModifiedAt = now,
        };
    }

    /// <summary>
    /// The next physical process of this execution. The ordinal is the only one the parent's head admits, and the
    /// worker fence is this worker's own — a superseded worker is REFUSED here rather than admitted, which is the
    /// intended outcome: it must not append a process to a run it no longer owns.
    /// </summary>
    private static WorkflowRunHarnessProcessAttempt AppendedAttempt(NativeRecordCaptureRequest request, WorkflowRunHarnessExecution execution)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunHarnessProcessAttempt
        {
            Id = Guid.NewGuid(), TeamId = request.TeamId, AgentRunId = request.AgentRunId, ExecutionId = execution.Id,
            AttemptOrdinal = execution.NextAttemptOrdinal, WorkerFenceEpoch = request.WorkerFenceEpoch,
            RunnerLocatorJson = request.RunnerLocatorJson, State = HarnessProcessAttemptState.Running,
            ClaimFence = 0, Revision = 1, StartedAt = now, LastObservedAt = now, CreatedAt = now, LastModifiedAt = now,
        };
    }

    /// <summary>
    /// The contract check <see cref="NativeRecordCapture"/> exists to make possible: a capture is validated against
    /// the wire shape BEFORE anything is persisted, rather than discovering at read time that the two drifted. Batch
    /// wide, because a record and the projections citing it are one transaction — dropping one and keeping the rest
    /// would leave a projection grounded in nothing, which the database refuses anyway.
    /// </summary>
    private static void EnsureContractual(NativeRecordBatch batch)
    {
        var errors = ContractErrors(batch).Take(ReportedContractErrors).ToList();

        if (errors.Count > 0)
            throw new NativeRecordContractException($"A captured frame or its projection does not satisfy the data contract and was not persisted: {string.Join("; ", errors)}");
    }

    private static IEnumerable<string> ContractErrors(NativeRecordBatch batch)
    {
        foreach (var error in batch.Records.SelectMany(capture => capture.Frame.Validate())) yield return $"record: {error}";
        foreach (var error in batch.Events.SelectMany(projection => projection.Validate())) yield return $"projection: {error}";
        foreach (var error in batch.ModelCalls.SelectMany(projection => projection.Validate())) yield return $"model call: {error}";

        // The handle is the authority on which execution this opening writes into; the projection merely repeats it.
        // A disagreement is caught here rather than by the composite foreign key at commit, where it would take the
        // whole batch down as an opaque constraint violation.
        foreach (var projection in batch.Events.Where(projection => projection.ExecutionId != batch.Handle.ExecutionId))
            yield return $"projection {projection.EventId} names execution {projection.ExecutionId}, which is not the one this capture opened";
    }

    private static WorkflowRunNativeRecord RecordRow(NativeRecordCaptureHandle handle, NativeRecordCapture capture)
    {
        var frame = capture.Frame;

        return new WorkflowRunNativeRecord
        {
            Id = frame.RecordId, TeamId = handle.TeamId, AgentRunId = handle.AgentRunId, ExecutionId = handle.ExecutionId,
            AttemptId = handle.AttemptId, StreamId = frame.StreamId, Ordinal = frame.Ordinal, Channel = frame.Channel,
            NativeType = frame.NativeType, NativeSchema = frame.NativeSchema, NativeSchemaVersion = frame.NativeSchemaVersion,
            OccurredAt = frame.OccurredAt, IngestedAt = frame.IngestedAt,
            SourceOffsetBytes = frame.ByteOffset, SourceLengthBytes = frame.ByteLength, SourceEndOffsetBytes = frame.ByteEndOffset,
            InlinePayload = frame.InlinePayload, PayloadRefJson = ArtifactRefJson(frame.PayloadRef),
            DigestAlgorithm = frame.DigestAlgorithm, Digest = frame.Digest, SizeBytes = frame.SizeBytes,
            PayloadEncoding = frame.Encoding, Redaction = frame.Redaction, IsFinal = frame.IsFinal,
            Normalization = capture.Normalization, NormalizationErrorCode = capture.NormalizationErrorCode,
            NormalizationErrorMessage = capture.NormalizationErrorMessage,
            ContractVersion = frame.ContractVersion, CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    private static WorkflowRunSemanticEvent EventRow(NativeRecordCaptureHandle handle, AgentSemanticEventV1 projection)
    {
        var now = DateTimeOffset.UtcNow;

        return new WorkflowRunSemanticEvent
        {
            Id = projection.EventId, TeamId = handle.TeamId, AgentRunId = handle.AgentRunId,
            ExecutionId = projection.ExecutionId, SourceNativeRecordIds = projection.SourceNativeRecordIds.ToArray(),
            EventType = projection.EventType, EventSchemaVersion = projection.EventSchemaVersion,
            SessionId = projection.SessionId, TurnId = projection.TurnId, StepId = projection.StepId,
            ModelCallId = projection.ModelCallId, ToolCallId = projection.ToolCallId,
            CorrelationId = projection.CorrelationId, CausationId = projection.CausationId,
            Necessity = projection.Necessity, ProjectionQuality = projection.ProjectionQuality,
            PayloadRefJson = ArtifactRefJson(projection.PayloadRef), ContractVersion = projection.ContractVersion,
            ProjectedAt = now, CreatedAt = now,
        };
    }

    /// <summary>The ref arm of the payload XOR, serialized for its jsonb column. Null stays null — the inline arm is the one the pump produces today, and dropping a ref a caller DID hand in would leave both arms null and be refused at commit.</summary>
    private static string? ArtifactRefJson(WorkflowRunArtifactRefV1? payloadRef) =>
        payloadRef is null ? null : JsonSerializer.Serialize(payloadRef, AgentJson.Options);

    private sealed record RunScope(Guid? WorkflowRunId);
}

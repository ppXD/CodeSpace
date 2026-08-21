using System.Data;
using System.Data.Common;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace CodeSpace.Core.Services.Workflows.ToolCalls;

/// <summary>
/// Projects bounded governed side-effect facts through an exact source-id anti-join. Source rows are share-locked,
/// and a transaction-scoped WorkflowRun lock plus the 0141 unique source index makes overlapping sweeps idempotent.
/// The projector never selects argument/result/error bodies and performs no artifact I/O.
/// </summary>
public sealed class WorkflowRunToolCallProjector : IWorkflowRunToolCallProjector
{
    internal const string SourceKind = "tool-call-ledger/v1";
    internal const string AdapterToolKind = "governed-tool-call/v1";
    internal const string Purpose = "agent.governed-side-effect/v1";
    internal const string FailedOutcomeUnknown = "ledger-failed-outcome-unknown";
    internal const string GovernanceDenied = "governance-denied";
    internal const string ApprovalExpired = "approval-expired";
    private const int MaxBatchSize = 1000;
    private readonly CodeSpaceDbContext _db;

    /// <summary>
    /// Candidate SQL is shared with the true-Postgres plan pin. The partial-index predicate is textually exact;
    /// AdmissionOrdinal is the gap-tolerant source rank, never a cursor or dense re-numbering. Selected fields are
    /// bounded metadata only: the ledger payload, error, hash, approval bearer and decision envelope never enter it.
    /// </summary>
    internal const string CandidateSql = """
        SELECT ledger.id,
               ledger.team_id,
               ledger.agent_run_id,
               scope.workflow_run_id,
               scope.node_id,
               scope.iteration_key,
               ledger.admission_ordinal,
               ledger.tool_kind,
               ledger.status,
               ledger.created_date,
               ledger.last_modified_date
        FROM tool_call_ledger AS ledger
        CROSS JOIN LATERAL (
            SELECT agent.workflow_run_id,
                   agent.node_id,
                   agent.iteration_key
            FROM agent_run AS agent
            INNER JOIN workflow_run AS run
                ON run.id = agent.workflow_run_id
               AND run.team_id = agent.team_id
            WHERE agent.id = ledger.agent_run_id
              AND agent.team_id = ledger.team_id
            FOR SHARE OF agent, run
        ) AS scope
        WHERE ledger.admission_ordinal IS NOT NULL
          AND ledger.tool_kind <> 'decision.request'
          AND btrim(ledger.tool_kind) <> ''
          AND char_length(ledger.tool_kind) <= 200
          AND (scope.node_id IS NULL OR char_length(scope.node_id) <= 256)
          AND char_length(scope.iteration_key) <= 1024
          AND ledger.status IN ('Succeeded', 'Failed', 'Denied', 'Expired')
          AND NOT EXISTS (
              SELECT 1
              FROM workflow_run_tool_call AS projected
              WHERE projected.team_id = ledger.team_id
                AND projected.workflow_run_id = scope.workflow_run_id
                AND projected.source_kind = 'tool-call-ledger/v1'
                AND projected.source_correlation_id = ledger.id
          )
        ORDER BY ledger.created_date, ledger.id
        LIMIT @batch_size
        FOR SHARE OF ledger
        """;

    public WorkflowRunToolCallProjector(CodeSpaceDbContext db) => _db = db;

    public async Task<WorkflowRunToolCallProjectionResult> SweepAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize <= 0 || batchSize > MaxBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize), $"Batch size must be between 1 and {MaxBatchSize}.");

        var ownsTransaction = _db.Database.CurrentTransaction == null;
        await using var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;
        var candidates = await ReadCandidatesAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var projected = await ProjectAsync(candidates, cancellationToken).ConfigureAwait(false);
        var diagnostics = await ReadDiagnosticsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        if (ownsTransaction) await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return diagnostics with { CallsProjected = projected };
    }

    private async Task<int> ProjectAsync(List<Candidate> candidates, CancellationToken cancellationToken)
    {
        if (candidates.Count == 0) return 0;
        await TakeRunLocksAsync(candidates.Select(value => value.WorkflowRunId), cancellationToken).ConfigureAwait(false);

        var runIds = candidates.Select(value => value.WorkflowRunId).Distinct().ToArray();
        var sourceIds = candidates.Select(value => value.SourceId).Distinct().ToArray();
        var admitted = (await _db.WorkflowRunToolCall.AsNoTracking()
            .Where(value => value.SourceKind == SourceKind && value.SourceCorrelationId != null
                && runIds.Contains(value.WorkflowRunId) && sourceIds.Contains(value.SourceCorrelationId.Value))
            .Select(value => new SourceIdentity(value.TeamId, value.WorkflowRunId, value.SourceCorrelationId!.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        candidates = candidates.Where(value => !admitted.Contains(SourceIdentity.For(value))).ToList();
        if (candidates.Count == 0) return 0;

        var projections = candidates.Select(BuildProjection).ToArray();
        _db.WorkflowRunToolCall.AddRange(projections.Select(value => value.Call));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        _db.WorkflowRunToolCallAttempt.AddRange(projections.Select(value => value.Attempt));
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        foreach (var projection in projections) CompleteAttempt(projection);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        var callIds = projections.Select(value => value.Call.Id).ToArray();
        var closures = projections.ToDictionary(value => value.Call.Id, value => value.Outcome);
        foreach (var projection in projections)
        {
            _db.Entry(projection.Call).State = EntityState.Detached;
            _db.Entry(projection.Attempt).State = EntityState.Detached;
        }
        var calls = await _db.WorkflowRunToolCall.Where(value => callIds.Contains(value.Id)).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var call in calls) CloseCall(call, closures[call.Id]);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return projections.Length;
    }

    private static Projection BuildProjection(Candidate candidate)
    {
        var outcome = Outcome.For(candidate);
        var callId = Guid.NewGuid();
        var call = new WorkflowRunToolCall
        {
            Id = callId, TeamId = candidate.TeamId, WorkflowRunId = candidate.WorkflowRunId, NodeId = candidate.NodeId,
            IterationKey = candidate.IterationKey, CallOrdinal = candidate.AdmissionOrdinal, Purpose = Purpose,
            ToolKind = AdapterToolKind, ToolName = candidate.RawToolKind, EffectClass = ToolCallEffectClass.SideEffecting,
            ArgumentsRedaction = NativeRecordRedaction.Withheld, SourceKind = SourceKind, SourceCorrelationId = candidate.SourceId,
            CaptureSource = SourceKind, CaptureCompleteness = WorkflowRunCaptureCompleteness.Unavailable,
            State = ToolCallState.Pending, AttemptCount = 0, NextAttemptOrdinal = 1, Revision = 1,
            SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedAt = candidate.CreatedAt, LastModifiedAt = candidate.CreatedAt,
        };
        var attempt = new WorkflowRunToolCallAttempt
        {
            Id = Guid.NewGuid(), TeamId = candidate.TeamId, WorkflowRunId = candidate.WorkflowRunId, ToolCallId = callId,
            AttemptOrdinal = 1, Status = ToolCallAttemptStatus.Pending, ResultRedaction = NativeRecordRedaction.Withheld,
            CaptureSource = SourceKind, CaptureCompleteness = WorkflowRunCaptureCompleteness.Unavailable,
            // The legacy ledger has no provider-dispatch timestamp. Admission is the only durable lower bound, not a
            // claim of exact transport latency; Unavailable capture makes the missing payload/timing evidence visible.
            StartedAt = candidate.CreatedAt, Revision = 1, SchemaVersion = WorkflowRunDataContract.CurrentVersion,
            CreatedAt = candidate.CreatedAt, LastModifiedAt = candidate.CreatedAt,
        };
        return new Projection(call, attempt, outcome);
    }

    private static void CompleteAttempt(Projection projection)
    {
        projection.Attempt.Status = projection.Outcome.AttemptStatus;
        projection.Attempt.ErrorCode = projection.Outcome.ErrorCode;
        projection.Attempt.CompletedAt = projection.Outcome.TerminalAt;
        projection.Attempt.LastModifiedAt = projection.Outcome.TerminalAt;
        projection.Attempt.Revision++;
    }

    private static void CloseCall(WorkflowRunToolCall call, Outcome outcome)
    {
        call.State = outcome.CallState;
        call.ErrorCode = outcome.CallState == ToolCallState.Abandoned ? outcome.ErrorCode : null;
        call.TerminalAt = outcome.TerminalAt;
        call.LastModifiedAt = outcome.TerminalAt;
        call.Revision++;
    }

    private async Task<List<Candidate>> ReadCandidatesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.CommandText = CandidateSql;
        command.Transaction = _db.Database.CurrentTransaction!.GetDbTransaction();
        AddParameter(command, "batch_size", DbType.Int32, batchSize);
        var candidates = new List<Candidate>(batchSize);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            candidates.Add(ReadCandidate(reader));
        return candidates;
    }

    private async Task<WorkflowRunToolCallProjectionResult> ReadDiagnosticsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var observed = await _db.Database.SqlQuery<DiagnosticSource>($$"""
            WITH observed AS MATERIALIZED (
                SELECT id, team_id, agent_run_id, admission_ordinal, tool_kind, status
                FROM tool_call_ledger
                ORDER BY id DESC
                LIMIT {{batchSize}}
            )
            SELECT source.admission_ordinal AS admission_ordinal,
                   source.tool_kind AS tool_kind,
                   source.status AS source_status,
                   agent.id IS NOT NULL AS agent_scope_exists,
                   agent.workflow_run_id IS NULL AS is_standalone,
                   run.id IS NOT NULL AS workflow_scope_exists,
                   agent.status AS agent_status,
                   char_length(source.tool_kind) BETWEEN 1 AND 200
                       AND (agent.node_id IS NULL OR char_length(agent.node_id) <= 256)
                       AND (agent.iteration_key IS NULL OR char_length(agent.iteration_key) <= 1024) AS identity_fits
            FROM observed AS source
            LEFT JOIN agent_run AS agent
                ON agent.id = source.agent_run_id
               AND agent.team_id = source.team_id
            LEFT JOIN workflow_run AS run
                ON run.id = agent.workflow_run_id
               AND run.team_id = source.team_id
            ORDER BY source.id DESC
            """).ToListAsync(cancellationToken).ConfigureAwait(false);

        var legacy = observed.Count(value => value.AdmissionOrdinal == null);
        var decisions = observed.Count(value => value.AdmissionOrdinal != null && value.ToolKind == DecisionToolKinds.DecisionRequest);
        var governed = observed.Where(value => value.AdmissionOrdinal != null && value.ToolKind != DecisionToolKinds.DecisionRequest).ToArray();
        var standalone = governed.Count(value => value.AgentScopeExists && value.IsStandalone);
        var invalidScope = governed.Count(value => !value.AgentScopeExists || (!value.IsStandalone && !value.WorkflowScopeExists));
        var invalidFacts = governed.Count(value => !value.IdentityFits || !KnownSourceStatus(value.SourceStatus) || (value.AgentScopeExists && !KnownAgentStatus(value.AgentStatus)));
        var deferred = governed.Count(value => value.AgentScopeExists && value.WorkflowScopeExists && value.IdentityFits
            && LiveSourceStatus(value.SourceStatus));
        return new WorkflowRunToolCallProjectionResult
        {
            DiagnosticRowsObserved = observed.Count, LegacyUnorderedSourcesObserved = legacy, DecisionSourcesObserved = decisions,
            StandaloneSourcesObserved = standalone, InvalidScopeSourcesObserved = invalidScope, DeferredLiveSourcesObserved = deferred,
            InvalidSourceFactsObserved = invalidFacts,
        };
    }

    private async Task TakeRunLocksAsync(IEnumerable<Guid> runIds, CancellationToken cancellationToken)
    {
        foreach (var runId in runIds.Distinct().OrderBy(value => value))
            await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({runId.ToString()}, 141))", cancellationToken).ConfigureAwait(false);
    }

    private static Candidate ReadCandidate(DbDataReader reader) => new()
    {
        SourceId = reader.GetGuid(0), TeamId = reader.GetGuid(1), AgentRunId = reader.GetGuid(2), WorkflowRunId = reader.GetGuid(3),
        NodeId = reader.IsDBNull(4) ? null : reader.GetString(4), IterationKey = reader.GetString(5), AdmissionOrdinal = reader.GetInt64(6),
        RawToolKind = reader.GetString(7), SourceStatus = reader.GetString(8),
        CreatedAt = reader.GetFieldValue<DateTimeOffset>(9), SourceModifiedAt = reader.GetFieldValue<DateTimeOffset>(10),
    };

    private static void AddParameter(DbCommand command, string name, DbType type, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.DbType = type;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static bool KnownSourceStatus(string status) => status is nameof(ToolCallLedgerStatus.Pending) or nameof(ToolCallLedgerStatus.Succeeded)
        or nameof(ToolCallLedgerStatus.Failed) or nameof(ToolCallLedgerStatus.Denied) or nameof(ToolCallLedgerStatus.AwaitingApproval)
        or nameof(ToolCallLedgerStatus.Running) or nameof(ToolCallLedgerStatus.Expired);
    private static bool LiveSourceStatus(string status) => status is nameof(ToolCallLedgerStatus.Pending) or nameof(ToolCallLedgerStatus.AwaitingApproval) or nameof(ToolCallLedgerStatus.Running);
    private static bool KnownAgentStatus(string? status) => status is nameof(AgentRunStatus.Queued) or nameof(AgentRunStatus.Running)
        or nameof(AgentRunStatus.Succeeded) or nameof(AgentRunStatus.Failed) or nameof(AgentRunStatus.Cancelled)
        or nameof(AgentRunStatus.TimedOut) or nameof(AgentRunStatus.NeedsReview);
    private static DateTimeOffset Latest(params DateTimeOffset?[] values) => values.OfType<DateTimeOffset>().Max();

    private sealed class Candidate
    {
        public Guid SourceId { get; init; }
        public Guid TeamId { get; init; }
        public Guid AgentRunId { get; init; }
        public Guid WorkflowRunId { get; init; }
        public string? NodeId { get; init; }
        public string IterationKey { get; init; } = string.Empty;
        public long AdmissionOrdinal { get; init; }
        public string RawToolKind { get; init; } = string.Empty;
        public string SourceStatus { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
        public DateTimeOffset SourceModifiedAt { get; init; }
    }
    private sealed record Projection(WorkflowRunToolCall Call, WorkflowRunToolCallAttempt Attempt, Outcome Outcome);
    private readonly record struct SourceIdentity(Guid TeamId, Guid WorkflowRunId, Guid SourceId)
    {
        public static SourceIdentity For(Candidate value) => new(value.TeamId, value.WorkflowRunId, value.SourceId);
    }

    private sealed record Outcome(ToolCallAttemptStatus AttemptStatus, ToolCallState CallState, string? ErrorCode, DateTimeOffset TerminalAt)
    {
        public static Outcome For(Candidate candidate)
        {
            var sourceTerminalAt = Latest(candidate.CreatedAt, candidate.SourceModifiedAt);
            return candidate.SourceStatus switch
            {
                nameof(ToolCallLedgerStatus.Succeeded) => new(ToolCallAttemptStatus.Succeeded, ToolCallState.Completed, null, sourceTerminalAt),
                nameof(ToolCallLedgerStatus.Failed) => new(ToolCallAttemptStatus.Indeterminate, ToolCallState.Abandoned, FailedOutcomeUnknown, sourceTerminalAt),
                nameof(ToolCallLedgerStatus.Denied) => new(ToolCallAttemptStatus.Denied, ToolCallState.Completed, GovernanceDenied, sourceTerminalAt),
                nameof(ToolCallLedgerStatus.Expired) => new(ToolCallAttemptStatus.Denied, ToolCallState.Completed, ApprovalExpired, sourceTerminalAt),
                _ => throw new InvalidOperationException($"Unsupported admitted ToolCallLedger status '{candidate.SourceStatus}'."),
            };
        }
    }

    private sealed class DiagnosticSource
    {
        public long? AdmissionOrdinal { get; set; }
        public string ToolKind { get; set; } = string.Empty;
        public string SourceStatus { get; set; } = string.Empty;
        public bool AgentScopeExists { get; set; }
        public bool IsStandalone { get; set; }
        public bool WorkflowScopeExists { get; set; }
        public string? AgentStatus { get; set; }
        public bool IdentityFits { get; set; }
    }
}

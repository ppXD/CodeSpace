using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

/// <summary>
/// Projects terminal interaction facts through source-id anti-joins. A transaction-scoped run lock makes
/// overlapping sweeps deterministic; the database admission trigger remains the final tenant/scope/source authority.
/// This projection is telemetry-only and is not read by model execution, completion, or terminal authority.
/// </summary>
public sealed class WorkflowRunModelCallProjector : IWorkflowRunModelCallProjector
{
    internal const string SourceKind = "workflow-run-record/v1";
    private const int MaxBatchSize = 1000;
    private readonly CodeSpaceDbContext _db;

    public WorkflowRunModelCallProjector(CodeSpaceDbContext db) => _db = db;

    public async Task<WorkflowRunModelCallProjectionResult> SweepAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize <= 0 || batchSize > MaxBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize), $"Batch size must be between 1 and {MaxBatchSize}.");

        var ownsTransaction = _db.Database.CurrentTransaction == null;
        await using var transaction = ownsTransaction ? await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false) : null;

        var projected = await ProjectTerminalsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var lateStarts = await AttachLateStartsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        if (ownsTransaction) await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkflowRunModelCallProjectionResult(projected, lateStarts);
    }

    private async Task<int> ProjectTerminalsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await (from record in _db.WorkflowRunRecord.AsNoTracking()
                                join run in _db.WorkflowRun.AsNoTracking() on record.RunId equals run.Id
                                where record.CorrelationId != null
                                      && (record.RecordType == WorkflowRunRecordTypes.InteractionCompleted || record.RecordType == WorkflowRunRecordTypes.InteractionFailed)
                                      && !_db.WorkflowRunModelCallAttempt.Any(attempt => attempt.SourceTerminalRecordId == record.Id)
                                orderby record.OccurredAt, record.Id
                                select new TerminalCandidate(record.Id, record.Sequence, record.RunId, run.TeamId, run.ActorId ?? run.CreatedBy,
                                    record.NodeId, record.IterationKey, record.CorrelationId!.Value, record.OccurredAt, record.RecordType, record.PayloadJson))
            .Take(batchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        await TakeRunLocksAsync(candidates.Select(value => value.RunId), cancellationToken).ConfigureAwait(false);
        var candidateIds = candidates.Select(value => value.RecordId).ToArray();
        var admitted = (await _db.WorkflowRunModelCallAttempt.AsNoTracking().Where(value => value.SourceTerminalRecordId != null && candidateIds.Contains(value.SourceTerminalRecordId.Value))
            .Select(value => value.SourceTerminalRecordId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        candidates = candidates.Where(value => !admitted.Contains(value.RecordId)).ToList();
        if (candidates.Count == 0) return 0;

        var scopes = candidates.Select(SourceScope.For).Distinct().ToArray();
        var starts = await ReadStartsAsync(scopes, cancellationToken).ConfigureAwait(false);
        var runIds = candidates.Select(value => value.RunId).Distinct().ToArray();
        var correlationIds = candidates.Select(value => value.CorrelationId).Distinct().ToArray();
        var calls = await _db.WorkflowRunModelCall.Where(value => value.SourceKind == SourceKind && value.SourceCorrelationId != null
                && runIds.Contains(value.WorkflowRunId) && correlationIds.Contains(value.SourceCorrelationId.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var callsBySource = calls.ToDictionary(value => new SourceScope(value.WorkflowRunId, value.NodeId, value.IterationKey, value.SourceCorrelationId!.Value));
        var callIds = calls.Select(value => value.Id).ToArray();
        var ordinals = callIds.Length == 0 ? new Dictionary<Guid, int>() : await _db.WorkflowRunModelCallAttempt.AsNoTracking()
            .Where(value => callIds.Contains(value.ModelCallId)).GroupBy(value => value.ModelCallId)
            .ToDictionaryAsync(group => group.Key, group => group.Max(value => value.AttemptOrdinal), cancellationToken).ConfigureAwait(false);
        var startIds = starts.Values.Select(value => value.Id).ToArray();
        var usedStarts = startIds.Length == 0 ? [] : (await _db.WorkflowRunModelCallAttempt.AsNoTracking()
            .Where(value => value.SourceStartedRecordId != null && startIds.Contains(value.SourceStartedRecordId.Value))
            .Select(value => value.SourceStartedRecordId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        foreach (var candidate in candidates)
        {
            var source = SourceScope.For(candidate);
            starts.TryGetValue(source, out var started);
            var terminalPayload = ParsedPayload.Parse(candidate.PayloadJson);
            var startedPayload = started == null ? null : ParsedPayload.Parse(started.PayloadJson);
            var input = new ProjectionInput(candidate, started, startedPayload, terminalPayload);
            if (!callsBySource.TryGetValue(source, out var call))
            {
                call = BuildCall(input);
                callsBySource.Add(source, call);
                ordinals.Add(call.Id, 0);
                _db.WorkflowRunModelCall.Add(call);
            }

            var ordinal = ordinals[call.Id] + 1;
            ordinals[call.Id] = ordinal;
            Guid? startedRecordId = started != null && usedStarts.Add(started.Id) ? started.Id : null;
            _db.WorkflowRunModelCallAttempt.Add(BuildAttempt(input, call.Id, ordinal, startedRecordId));
        }

        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Count;
    }

    private async Task<int> AttachLateStartsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await (from attempt in _db.WorkflowRunModelCallAttempt.AsNoTracking()
                                join call in _db.WorkflowRunModelCall.AsNoTracking() on attempt.ModelCallId equals call.Id
                                where call.SourceKind == SourceKind && attempt.SourceTerminalRecordId != null && attempt.SourceStartedRecordId == null
                                orderby attempt.CreatedDate, attempt.Id
                                select new LateStartCandidate(attempt.Id, attempt.SourceEvidenceRevision, attempt.SourceTerminalRecordId!.Value,
                                    call.Id, call.TeamId, call.WorkflowRunId, call.NodeId, call.IterationKey, call.SourceCorrelationId!.Value))
            .Take(batchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        await TakeRunLocksAsync(candidates.Select(value => value.WorkflowRunId), cancellationToken).ConfigureAwait(false);
        var attemptIds = candidates.Select(value => value.AttemptId).ToArray();
        var attempts = await _db.WorkflowRunModelCallAttempt.Where(value => attemptIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        candidates = candidates.Where(value => attempts.TryGetValue(value.AttemptId, out var attempt)
            && attempt.SourceStartedRecordId == null && attempt.SourceEvidenceRevision == value.SourceEvidenceRevision).ToList();
        if (candidates.Count == 0) return 0;

        var callIds = candidates.Select(value => value.ModelCallId).Distinct().ToArray();
        var calls = await _db.WorkflowRunModelCall.Where(value => callIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        var scopes = candidates.Select(value => new SourceScope(value.WorkflowRunId, value.NodeId, value.IterationKey, value.CorrelationId)).Distinct().ToArray();
        var starts = await ReadStartsAsync(scopes, cancellationToken).ConfigureAwait(false);
        var startIds = starts.Values.Select(value => value.Id).ToArray();
        var usedStarts = startIds.Length == 0 ? [] : (await _db.WorkflowRunModelCallAttempt.AsNoTracking()
            .Where(value => value.SourceStartedRecordId != null && startIds.Contains(value.SourceStartedRecordId.Value))
            .Select(value => value.SourceStartedRecordId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var terminalIds = candidates.Select(value => value.TerminalRecordId).Distinct().ToArray();
        var terminals = await _db.WorkflowRunRecord.AsNoTracking().Where(value => terminalIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);

        var changed = 0;
        foreach (var candidate in candidates)
        {
            var source = new SourceScope(candidate.WorkflowRunId, candidate.NodeId, candidate.IterationKey, candidate.CorrelationId);
            if (!starts.TryGetValue(source, out var started) || !usedStarts.Add(started.Id)
                || !attempts.TryGetValue(candidate.AttemptId, out var attempt) || !calls.TryGetValue(candidate.ModelCallId, out var call)
                || !terminals.TryGetValue(candidate.TerminalRecordId, out var terminal)) continue;
            ApplyStarted(call, attempt, new LateStartEvidence(started, ParsedPayload.Parse(started.PayloadJson), terminal, ParsedPayload.Parse(terminal.PayloadJson)));
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private async Task<Dictionary<SourceScope, SourceStarted>> ReadStartsAsync(IReadOnlyCollection<SourceScope> scopes, CancellationToken cancellationToken)
    {
        var runIds = scopes.Select(value => value.RunId).Distinct().ToArray();
        var correlationIds = scopes.Select(value => value.CorrelationId).Distinct().ToArray();
        var candidates = await _db.WorkflowRunRecord.AsNoTracking()
            .Where(record => record.RecordType == WorkflowRunRecordTypes.InteractionStarted && record.CorrelationId != null
                && runIds.Contains(record.RunId) && correlationIds.Contains(record.CorrelationId.Value))
            .Select(record => new SourceStarted(record.Id, record.RunId, record.NodeId, record.IterationKey, record.CorrelationId!.Value,
                record.Sequence, record.OccurredAt, record.PayloadJson)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var requested = scopes.ToHashSet();
        return candidates.Where(value => requested.Contains(SourceScope.For(value))).GroupBy(SourceScope.For)
            .ToDictionary(group => group.Key, group => group.OrderBy(value => value.Sequence).First());
    }

    private static WorkflowRunModelCall BuildCall(ProjectionInput input) => new()
    {
        // Source allocation order is intentionally used only as a stable, gap-tolerant within-run display ordinal;
        // it is never a projection cursor or commit watermark.
        Id = Guid.NewGuid(), TeamId = input.Candidate.TeamId, WorkflowRunId = input.Candidate.RunId, NodeId = input.Candidate.NodeId,
        IterationKey = input.Candidate.IterationKey, CallOrdinal = Math.Max(1, input.Candidate.Sequence), SourceKind = SourceKind,
        SourceCorrelationId = input.Candidate.CorrelationId, Purpose = Purpose(input.StartedPayload?.Kind ?? input.TerminalPayload.Kind),
        RequestedProvider = Limit(input.StartedPayload?.Provider, 100), RequestedModel = Limit(input.StartedPayload?.Model, 500),
        CaptureSource = SourceKind, CaptureCompleteness = Completeness(input.StartedPayload, input.TerminalPayload), SchemaVersion = WorkflowRunDataContract.CurrentVersion,
        CreatedDate = input.StartedRecord?.OccurredAt ?? input.Candidate.OccurredAt, CreatedBy = input.Candidate.ActorId,
        LastModifiedDate = input.Candidate.OccurredAt, LastModifiedBy = input.Candidate.ActorId,
    };

    private static WorkflowRunModelCallAttempt BuildAttempt(ProjectionInput input, Guid callId, int ordinal, Guid? startedRecordId) => new()
    {
        Id = Guid.NewGuid(), TeamId = input.Candidate.TeamId, WorkflowRunId = input.Candidate.RunId, ModelCallId = callId, AttemptOrdinal = ordinal,
        SourceStartedRecordId = startedRecordId, SourceTerminalRecordId = input.Candidate.RecordId, SourceEvidenceRevision = 1,
        EffectiveProvider = Limit(input.TerminalPayload.Provider, 100), EffectiveModel = Limit(input.TerminalPayload.Model, 500),
        ResponseArtifactId = input.TerminalPayload.OutputArtifactId, Status = Status(input.Candidate.RecordType, input.TerminalPayload.FailureKind),
        ErrorCode = Limit(input.TerminalPayload.ErrorCode, 200), FinishReason = Limit(input.TerminalPayload.FinishReason, 100), CaptureSource = SourceKind,
        CaptureCompleteness = Completeness(input.StartedPayload, input.TerminalPayload), InputTokens = input.TerminalPayload.InputTokens,
        OutputTokens = input.TerminalPayload.OutputTokens, StartedAt = Earlier(input.StartedRecord?.OccurredAt, input.Candidate.OccurredAt),
        CompletedAt = input.Candidate.OccurredAt, SchemaVersion = WorkflowRunDataContract.CurrentVersion,
        CreatedDate = input.StartedRecord?.OccurredAt ?? input.Candidate.OccurredAt, CreatedBy = input.Candidate.ActorId,
        LastModifiedDate = input.Candidate.OccurredAt, LastModifiedBy = input.Candidate.ActorId,
    };

    private static void ApplyStarted(WorkflowRunModelCall call, WorkflowRunModelCallAttempt attempt, LateStartEvidence evidence)
    {
        call.Purpose = Purpose(evidence.StartedPayload.Kind ?? evidence.TerminalPayload.Kind);
        call.RequestedProvider = Limit(evidence.StartedPayload.Provider, 100);
        call.RequestedModel = Limit(evidence.StartedPayload.Model, 500);
        call.CaptureCompleteness = Completeness(evidence.StartedPayload, evidence.TerminalPayload);
        attempt.SourceStartedRecordId = evidence.Started.Id;
        attempt.SourceEvidenceRevision++;
        attempt.StartedAt = Earlier(evidence.Started.OccurredAt, evidence.Terminal.OccurredAt);
        attempt.CaptureCompleteness = Completeness(evidence.StartedPayload, evidence.TerminalPayload);
    }

    private async Task TakeRunLocksAsync(IEnumerable<Guid> runIds, CancellationToken cancellationToken)
    {
        foreach (var runId in runIds.Distinct().OrderBy(value => value))
            await _db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({runId.ToString()}, 84))", cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset Earlier(DateTimeOffset? left, DateTimeOffset right) => left is { } value && value < right ? value : right;
    private static WorkflowRunCaptureCompleteness Completeness(ParsedPayload? started, ParsedPayload terminal) =>
        terminal.IsCorrupt || started is { IsCorrupt: true } ? WorkflowRunCaptureCompleteness.Corrupt : WorkflowRunCaptureCompleteness.Partial;
    private static string Purpose(string? kind) => string.IsNullOrWhiteSpace(kind) ? "unknown/v1" : Limit(kind.Trim().EndsWith("/v1", StringComparison.Ordinal) ? kind.Trim() : kind.Trim() + "/v1", 128)!;
    private static string Status(string recordType, string? failureKind) => recordType == WorkflowRunRecordTypes.InteractionCompleted
        ? "Succeeded" : string.Equals(failureKind, "cancelled", StringComparison.OrdinalIgnoreCase) ? "Cancelled" : "Failed";
    private static string? Limit(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length <= maxLength) return trimmed;
        var length = maxLength;
        if (length > 0 && char.IsHighSurrogate(trimmed[length - 1])) length--;
        return trimmed[..length];
    }

    private sealed record TerminalCandidate(Guid RecordId, long Sequence, Guid RunId, Guid TeamId, Guid ActorId, string? NodeId,
        string IterationKey, Guid CorrelationId, DateTimeOffset OccurredAt, string RecordType, string PayloadJson);
    private sealed record LateStartCandidate(Guid AttemptId, int SourceEvidenceRevision, Guid TerminalRecordId, Guid ModelCallId,
        Guid TeamId, Guid WorkflowRunId, string? NodeId, string IterationKey, Guid CorrelationId);
    private readonly record struct SourceScope(Guid RunId, string? NodeId, string IterationKey, Guid CorrelationId)
    {
        public static SourceScope For(TerminalCandidate value) => new(value.RunId, value.NodeId, value.IterationKey, value.CorrelationId);
        public static SourceScope For(SourceStarted value) => new(value.RunId, value.NodeId, value.IterationKey, value.CorrelationId);
    }
    private sealed record SourceStarted(Guid Id, Guid RunId, string? NodeId, string IterationKey, Guid CorrelationId, long Sequence,
        DateTimeOffset OccurredAt, string PayloadJson);
    private sealed record ProjectionInput(TerminalCandidate Candidate, SourceStarted? StartedRecord, ParsedPayload? StartedPayload, ParsedPayload TerminalPayload);
    private sealed record LateStartEvidence(SourceStarted Started, ParsedPayload StartedPayload, WorkflowRunRecord Terminal, ParsedPayload TerminalPayload);

    private sealed record ParsedPayload(bool IsCorrupt, string? Kind, string? Provider, string? Model,
        string? FailureKind, string? ErrorCode, string? FinishReason, long? InputTokens, long? OutputTokens, Guid? OutputArtifactId)
    {
        public static ParsedPayload Parse(string json)
        {
            try
            {
                using var document = JsonDocument.Parse(json);
                if (document.RootElement.ValueKind != JsonValueKind.Object) return Corrupt();
                var root = document.RootElement;
                var usage = root.TryGetProperty("usage", out var usageElement) && usageElement.ValueKind == JsonValueKind.Object ? usageElement : default;
                return new ParsedPayload(false, Text(root, "kind"), Text(root, "provider"), Text(root, "model"), Text(root, "failureKind"),
                    Text(root, "category") ?? Text(root, "failureKind"), usage.ValueKind == JsonValueKind.Object ? Text(usage, "finishReason") : null,
                    usage.ValueKind == JsonValueKind.Object ? NonNegativeLong(usage, "inputTokens") : null,
                    usage.ValueKind == JsonValueKind.Object ? NonNegativeLong(usage, "outputTokens") : null, ArtifactId(root, "output"));
            }
            catch (JsonException)
            {
                return Corrupt();
            }
        }

        private static ParsedPayload Corrupt() => new(true, null, null, null, null, null, null, null, null, null);
        private static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        private static long? NonNegativeLong(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var parsed) && parsed >= 0 ? parsed : null;
        private static Guid? ArtifactId(JsonElement root, string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Object
            && value.TryGetProperty("$artifact_id", out var id) && id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed) ? parsed : null;
    }
}

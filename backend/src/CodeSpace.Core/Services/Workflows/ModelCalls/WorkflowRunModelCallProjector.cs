using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

/// <summary>
/// Projects started and terminal interaction facts through source-id anti-joins. A transaction-scoped run lock makes
/// overlapping sweeps deterministic, late evidence attaches to the same attempt, and terminal runs settle orphaned
/// starts as indeterminate. The database admission trigger remains the final tenant/scope/source authority.
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

        var started = await ProjectStartsOnlyAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var lateTerminals = await AttachLateTerminalsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var projected = await ProjectTerminalsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var lateStarts = await AttachLateStartsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var orphanedStarts = await SettleOrphanedStartsAsync(batchSize, cancellationToken).ConfigureAwait(false);
        var bodyCaptures = await DeclareStartedBodyCapturesAsync(batchSize, cancellationToken).ConfigureAwait(false)
            + await DeclareBodyCapturesAsync(batchSize, cancellationToken).ConfigureAwait(false);
        if (ownsTransaction) await transaction!.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new WorkflowRunModelCallProjectionResult(projected, lateStarts, bodyCaptures, started, lateTerminals, orphanedStarts);
    }

    private async Task<int> ProjectStartsOnlyAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await (from record in _db.WorkflowRunRecord.AsNoTracking()
                                join run in _db.WorkflowRun.AsNoTracking() on record.RunId equals run.Id
                                where record.CorrelationId != null && record.RecordType == WorkflowRunRecordTypes.InteractionStarted
                                      && !_db.WorkflowRunModelCallAttempt.Any(attempt => attempt.SourceStartedRecordId == record.Id)
                                      && !_db.WorkflowRunModelCall.Any(call => call.TeamId == run.TeamId && call.WorkflowRunId == record.RunId
                                          && call.SourceKind == SourceKind && call.SourceCorrelationId == record.CorrelationId)
                                      && !_db.WorkflowRunRecord.Any(terminal => terminal.RunId == record.RunId && terminal.CorrelationId == record.CorrelationId
                                          && terminal.NodeId == record.NodeId && terminal.IterationKey == record.IterationKey
                                          && (terminal.RecordType == WorkflowRunRecordTypes.InteractionCompleted || terminal.RecordType == WorkflowRunRecordTypes.InteractionFailed))
                                orderby record.OccurredAt, record.Id
                                select new StartedCandidate(record.Id, record.Sequence, record.RunId, run.TeamId, run.ActorId ?? run.CreatedBy,
                                    record.NodeId, record.IterationKey, record.CorrelationId!.Value, record.OccurredAt, record.PayloadJson))
            .Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        await TakeRunLocksAsync(candidates.Select(value => value.RunId), cancellationToken).ConfigureAwait(false);
        var runIds = candidates.Select(value => value.RunId).Distinct().ToArray();
        var correlationIds = candidates.Select(value => value.CorrelationId).Distinct().ToArray();
        var admitted = (await _db.WorkflowRunModelCall.AsNoTracking().Where(value => value.SourceKind == SourceKind && value.SourceCorrelationId != null
                && runIds.Contains(value.WorkflowRunId) && correlationIds.Contains(value.SourceCorrelationId.Value))
            .Select(value => new SourceIdentity(value.TeamId, value.WorkflowRunId, value.SourceCorrelationId!.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        candidates = candidates.Where(value => !admitted.Contains(SourceIdentity.For(value)))
            .GroupBy(SourceIdentity.For).Select(group => group.OrderBy(value => value.OccurredAt).ThenBy(value => value.RecordId).First())
            .OrderBy(value => value.OccurredAt).ThenBy(value => value.RecordId).ToList();
        if (candidates.Count == 0) return 0;

        var terminalScopes = (await ReadTerminalsAsync(candidates.Select(SourceScope.For).Distinct().ToArray(), cancellationToken).ConfigureAwait(false)).Keys.ToHashSet();
        candidates = candidates.Where(value => !terminalScopes.Contains(SourceScope.For(value))).ToList();
        foreach (var candidate in candidates)
        {
            var payload = ParsedPayload.Parse(candidate.PayloadJson);
            var call = BuildStartedCall(candidate, payload);
            _db.WorkflowRunModelCall.Add(call);
            _db.WorkflowRunModelCallAttempt.Add(BuildStartedAttempt(candidate, payload, call.Id));
        }

        if (candidates.Count > 0) await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return candidates.Count;
    }

    private async Task<int> AttachLateTerminalsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await (from attempt in _db.WorkflowRunModelCallAttempt.AsNoTracking()
                                join call in _db.WorkflowRunModelCall.AsNoTracking() on attempt.ModelCallId equals call.Id
                                where call.SourceKind == SourceKind && attempt.SourceStartedRecordId != null && attempt.SourceTerminalRecordId == null
                                orderby attempt.CreatedDate, attempt.Id
                                select new LateTerminalCandidate(attempt.Id, attempt.SourceEvidenceRevision, call.Id, call.TeamId,
                                    call.WorkflowRunId, call.NodeId, call.IterationKey, call.SourceCorrelationId!.Value))
            .Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        await TakeRunLocksAsync(candidates.Select(value => value.WorkflowRunId), cancellationToken).ConfigureAwait(false);
        var attemptIds = candidates.Select(value => value.AttemptId).ToArray();
        var attempts = await _db.WorkflowRunModelCallAttempt.Where(value => attemptIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        candidates = candidates.Where(value => attempts.TryGetValue(value.AttemptId, out var attempt)
            && attempt.SourceTerminalRecordId == null && attempt.SourceEvidenceRevision == value.SourceEvidenceRevision).ToList();
        if (candidates.Count == 0) return 0;

        var callIds = candidates.Select(value => value.ModelCallId).Distinct().ToArray();
        var calls = await _db.WorkflowRunModelCall.Where(value => callIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        var startedIds = attempts.Values.Select(value => value.SourceStartedRecordId).OfType<Guid>().Distinct().ToArray();
        var startedPayloads = await _db.WorkflowRunRecord.AsNoTracking().Where(value => startedIds.Contains(value.Id))
            .Select(value => new { value.Id, value.PayloadJson }).ToDictionaryAsync(value => value.Id, value => value.PayloadJson, cancellationToken).ConfigureAwait(false);
        var scopes = candidates.Select(SourceScope.For).Distinct().ToArray();
        var terminals = await ReadTerminalsAsync(scopes, cancellationToken).ConfigureAwait(false);
        var terminalIds = terminals.Values.Select(value => value.Id).ToArray();
        var usedTerminals = terminalIds.Length == 0 ? [] : (await _db.WorkflowRunModelCallAttempt.AsNoTracking()
            .Where(value => value.SourceTerminalRecordId != null && terminalIds.Contains(value.SourceTerminalRecordId.Value))
            .Select(value => value.SourceTerminalRecordId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var artifactIds = terminals.Values.Select(value => ParsedPayload.Parse(value.PayloadJson).OutputArtifactId).OfType<Guid>().Distinct().ToArray();
        var teamIds = candidates.Select(value => value.TeamId).Distinct().ToArray();
        var artifacts = artifactIds.Length == 0 ? [] : (await _db.WorkflowArtifact.AsNoTracking().Where(value => teamIds.Contains(value.TeamId) && artifactIds.Contains(value.Id))
            .Select(value => new ArtifactIdentity(value.TeamId, value.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        var changed = 0;
        foreach (var candidate in candidates)
        {
            if (!terminals.TryGetValue(SourceScope.For(candidate), out var terminal) || !usedTerminals.Add(terminal.Id)
                || !attempts.TryGetValue(candidate.AttemptId, out var attempt) || !calls.TryGetValue(candidate.ModelCallId, out var call)
                || attempt.SourceStartedRecordId is not { } startedId || !startedPayloads.TryGetValue(startedId, out var startedJson)) continue;
            var startedPayload = ParsedPayload.Parse(startedJson);
            var terminalPayload = ParsedPayload.Parse(terminal.PayloadJson);
            var artifactId = terminalPayload.OutputArtifactId is { } id && artifacts.Contains(new ArtifactIdentity(candidate.TeamId, id)) ? id : (Guid?)null;
            ApplyTerminal(call, attempt, startedPayload, terminal, terminalPayload, artifactId);
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private async Task<int> SettleOrphanedStartsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await (from attempt in _db.WorkflowRunModelCallAttempt.AsNoTracking()
                                join call in _db.WorkflowRunModelCall.AsNoTracking() on attempt.ModelCallId equals call.Id
                                join run in _db.WorkflowRun.AsNoTracking() on new { call.TeamId, Id = call.WorkflowRunId } equals new { run.TeamId, run.Id }
                                where call.SourceKind == SourceKind && attempt.SourceStartedRecordId != null && attempt.SourceTerminalRecordId == null
                                      && attempt.Status == "Pending"
                                      && (run.Status == WorkflowRunStatus.Success || run.Status == WorkflowRunStatus.Failure
                                          || run.Status == WorkflowRunStatus.Cancelled)
                                orderby attempt.CreatedDate, attempt.Id
                                select new OrphanedStartCandidate(attempt.Id, attempt.SourceEvidenceRevision, call.Id, call.WorkflowRunId,
                                    run.CompletedAt ?? call.LastModifiedDate))
            .Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        await TakeRunLocksAsync(candidates.Select(value => value.WorkflowRunId), cancellationToken).ConfigureAwait(false);
        var attemptIds = candidates.Select(value => value.AttemptId).ToArray();
        var attempts = await _db.WorkflowRunModelCallAttempt.Where(value => attemptIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        var callIds = candidates.Select(value => value.ModelCallId).Distinct().ToArray();
        var calls = await _db.WorkflowRunModelCall.Where(value => callIds.Contains(value.Id)).ToDictionaryAsync(value => value.Id, cancellationToken).ConfigureAwait(false);
        var changed = 0;
        foreach (var candidate in candidates)
        {
            if (!attempts.TryGetValue(candidate.AttemptId, out var attempt) || !calls.TryGetValue(candidate.ModelCallId, out var call)
                || attempt.SourceTerminalRecordId != null || attempt.Status != "Pending" || attempt.SourceEvidenceRevision != candidate.SourceEvidenceRevision) continue;
            attempt.Status = "Indeterminate";
            attempt.ErrorCode = "TerminalCaptureMissing";
            attempt.CompletedAt = candidate.TerminalAt < attempt.StartedAt ? attempt.StartedAt : candidate.TerminalAt;
            attempt.SourceEvidenceRevision++;
            attempt.LastModifiedDate = attempt.CompletedAt.Value;
            call.LastModifiedDate = attempt.CompletedAt.Value;
            changed++;
        }

        if (changed > 0) await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return changed;
    }

    private async Task<int> ProjectTerminalsAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await (from record in _db.WorkflowRunRecord.AsNoTracking()
                                join run in _db.WorkflowRun.AsNoTracking() on record.RunId equals run.Id
                                where record.CorrelationId != null
                                      && (record.RecordType == WorkflowRunRecordTypes.InteractionCompleted || record.RecordType == WorkflowRunRecordTypes.InteractionFailed)
                                      && !_db.WorkflowRunModelCallAttempt.Any(attempt => attempt.SourceTerminalRecordId == record.Id)
                                      && !_db.WorkflowRunModelCall.Any(call => call.TeamId == run.TeamId && call.WorkflowRunId == record.RunId
                                          && call.SourceKind == SourceKind && call.SourceCorrelationId == record.CorrelationId)
                                orderby record.OccurredAt, record.Id
                                select new TerminalCandidate(record.Id, record.Sequence, record.RunId, run.TeamId, run.ActorId ?? run.CreatedBy,
                                    record.NodeId, record.IterationKey, record.CorrelationId!.Value, record.OccurredAt, record.RecordType, record.PayloadJson))
            .Take(batchSize)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        await TakeRunLocksAsync(candidates.Select(value => value.RunId), cancellationToken).ConfigureAwait(false);
        var runIds = candidates.Select(value => value.RunId).Distinct().ToArray();
        var correlationIds = candidates.Select(value => value.CorrelationId).Distinct().ToArray();
        var admitted = (await _db.WorkflowRunModelCall.AsNoTracking().Where(value => value.SourceKind == SourceKind && value.SourceCorrelationId != null
                && runIds.Contains(value.WorkflowRunId) && correlationIds.Contains(value.SourceCorrelationId.Value))
            .Select(value => new SourceIdentity(value.TeamId, value.WorkflowRunId, value.SourceCorrelationId!.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        candidates = candidates.Where(value => !admitted.Contains(SourceIdentity.For(value)))
            .GroupBy(SourceIdentity.For).Select(group => group.OrderBy(value => value.OccurredAt).ThenBy(value => value.RecordId).First())
            .OrderBy(value => value.OccurredAt).ThenBy(value => value.RecordId).ToList();
        if (candidates.Count == 0) return 0;

        var scopes = candidates.Select(SourceScope.For).Distinct().ToArray();
        var starts = await ReadStartsAsync(scopes, cancellationToken).ConfigureAwait(false);
        var startIds = starts.Values.Select(value => value.Id).ToArray();
        var usedStarts = startIds.Length == 0 ? [] : (await _db.WorkflowRunModelCallAttempt.AsNoTracking()
            .Where(value => value.SourceStartedRecordId != null && startIds.Contains(value.SourceStartedRecordId.Value))
            .Select(value => value.SourceStartedRecordId!.Value).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var inputs = candidates.Select(candidate =>
        {
            starts.TryGetValue(SourceScope.For(candidate), out var started);
            return new ProjectionInput(candidate, started, started == null ? null : ParsedPayload.Parse(started.PayloadJson), ParsedPayload.Parse(candidate.PayloadJson));
        }).ToArray();
        var artifactIds = inputs.Select(value => value.TerminalPayload.OutputArtifactId).OfType<Guid>().Distinct().ToArray();
        var teamIds = inputs.Select(value => value.Candidate.TeamId).Distinct().ToArray();
        var artifacts = artifactIds.Length == 0 ? [] : (await _db.WorkflowArtifact.AsNoTracking()
            .Where(value => teamIds.Contains(value.TeamId) && artifactIds.Contains(value.Id))
            .Select(value => new ArtifactIdentity(value.TeamId, value.Id)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();

        foreach (var input in inputs)
        {
            var call = BuildCall(input);
            Guid? startedRecordId = input.StartedRecord != null && usedStarts.Add(input.StartedRecord.Id) ? input.StartedRecord.Id : null;
            Guid? responseArtifactId = input.TerminalPayload.OutputArtifactId is { } artifactId
                && artifacts.Contains(new ArtifactIdentity(input.Candidate.TeamId, artifactId)) ? artifactId : null;
            _db.WorkflowRunModelCall.Add(call);
            _db.WorkflowRunModelCallAttempt.Add(BuildAttempt(input, call.Id, startedRecordId, responseArtifactId));
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

    /// <summary>
    /// Declares body work from stable source ids only. This runs in the projector transaction but deliberately never
    /// reads or writes artifact bytes: storage latency/failure belongs to the independently leased materializer, while
    /// this durable row keeps the immutable source discoverable until materialization reaches an honest outcome.
    /// </summary>
    private async Task<int> DeclareStartedBodyCapturesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await (from attempt in _db.WorkflowRunModelCallAttempt.AsNoTracking()
                                join call in _db.WorkflowRunModelCall.AsNoTracking() on attempt.ModelCallId equals call.Id
                                where call.SourceKind == SourceKind && attempt.SourceStartedRecordId != null
                                      && !_db.WorkflowRunModelCallBodyCapture.Any(value => value.ModelCallAttemptId == attempt.Id
                                          && value.BodyKind == WorkflowRunModelCallBodyKind.LogicalRequest)
                                orderby attempt.CreatedDate, attempt.Id
                                select new StartedBodyCaptureCandidate(call.TeamId, call.WorkflowRunId, call.Id, attempt.Id,
                                    attempt.SourceStartedRecordId!.Value))
            .Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        await TakeRunLocksAsync(candidates.Select(value => value.WorkflowRunId), cancellationToken).ConfigureAwait(false);
        var attemptIds = candidates.Select(value => value.ModelCallAttemptId).ToArray();
        var existing = (await _db.WorkflowRunModelCallBodyCapture.AsNoTracking().Where(value => attemptIds.Contains(value.ModelCallAttemptId)
                && value.BodyKind == WorkflowRunModelCallBodyKind.LogicalRequest)
            .Select(value => value.ModelCallAttemptId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var now = DateTimeOffset.UtcNow;
        var declared = 0;
        foreach (var candidate in candidates.Where(value => existing.Add(value.ModelCallAttemptId)))
        {
            _db.WorkflowRunModelCallBodyCapture.Add(new WorkflowRunModelCallBodyCapture
            {
                Id = Guid.NewGuid(), TeamId = candidate.TeamId, WorkflowRunId = candidate.WorkflowRunId, ModelCallId = candidate.ModelCallId,
                ModelCallAttemptId = candidate.ModelCallAttemptId, BodyKind = WorkflowRunModelCallBodyKind.LogicalRequest, SourceKind = SourceKind,
                SourceRecordId = candidate.SourceStartedRecordId, SourceProperty = "prompt", State = WorkflowRunModelCallBodyCaptureState.Pending,
                NextMaterializationAt = now, Revision = 1, CreatedAt = now, LastModifiedAt = now,
            });
            declared++;
        }

        if (declared > 0) await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return declared;
    }

    private async Task<int> DeclareBodyCapturesAsync(int batchSize, CancellationToken cancellationToken)
    {
        var candidates = await (from attempt in _db.WorkflowRunModelCallAttempt.AsNoTracking()
                                join call in _db.WorkflowRunModelCall.AsNoTracking() on attempt.ModelCallId equals call.Id
                                join terminal in _db.WorkflowRunRecord.AsNoTracking() on attempt.SourceTerminalRecordId equals terminal.Id
                                where call.SourceKind == SourceKind
                                      && ((attempt.SourceStartedRecordId != null && !_db.WorkflowRunModelCallBodyCapture.Any(value => value.ModelCallAttemptId == attempt.Id
                                              && value.BodyKind == WorkflowRunModelCallBodyKind.LogicalRequest))
                                          || (terminal.RecordType == WorkflowRunRecordTypes.InteractionCompleted
                                              && !_db.WorkflowRunModelCallBodyCapture.Any(value => value.ModelCallAttemptId == attempt.Id
                                                  && value.BodyKind == WorkflowRunModelCallBodyKind.AttemptResponse))
                                          || (terminal.RecordType == WorkflowRunRecordTypes.InteractionFailed
                                              && !_db.WorkflowRunModelCallBodyCapture.Any(value => value.ModelCallAttemptId == attempt.Id
                                                  && value.BodyKind == WorkflowRunModelCallBodyKind.AttemptError)))
                                orderby attempt.CreatedDate, attempt.Id
                                select new BodyCaptureCandidate(call.TeamId, call.WorkflowRunId, call.Id, attempt.Id,
                                    attempt.SourceStartedRecordId, attempt.SourceTerminalRecordId!.Value, terminal.RecordType, attempt.ResponseArtifactId))
            .Take(batchSize).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (candidates.Count == 0) return 0;

        await TakeRunLocksAsync(candidates.Select(value => value.WorkflowRunId), cancellationToken).ConfigureAwait(false);
        var attemptIds = candidates.Select(value => value.ModelCallAttemptId).ToArray();
        var existing = (await _db.WorkflowRunModelCallBodyCapture.AsNoTracking().Where(value => attemptIds.Contains(value.ModelCallAttemptId))
            .Select(value => new BodyCaptureIdentity(value.ModelCallAttemptId, value.BodyKind)).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var artifactIds = candidates.Select(value => value.ResponseArtifactId).OfType<Guid>().Distinct().ToArray();
        var teamIds = candidates.Select(value => value.TeamId).Distinct().ToArray();
        var artifacts = artifactIds.Length == 0 ? [] : await _db.WorkflowArtifact.AsNoTracking()
            .Where(value => teamIds.Contains(value.TeamId) && artifactIds.Contains(value.Id))
            .Select(value => new BodyArtifact(value.TeamId, value.Id, value.Sha256, value.SizeBytes, value.ContentType))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        var artifactsByIdentity = artifacts.ToDictionary(value => new ArtifactIdentity(value.TeamId, value.Id));
        var now = DateTimeOffset.UtcNow;
        var declared = 0;

        foreach (var candidate in candidates)
        {
            if (candidate.SourceStartedRecordId is { } startedRecordId
                && existing.Add(new BodyCaptureIdentity(candidate.ModelCallAttemptId, WorkflowRunModelCallBodyKind.LogicalRequest)))
            {
                _db.WorkflowRunModelCallBodyCapture.Add(PendingCapture(candidate, WorkflowRunModelCallBodyKind.LogicalRequest, startedRecordId, "prompt", now));
                declared++;
            }

            var kind = candidate.TerminalRecordType == WorkflowRunRecordTypes.InteractionCompleted
                ? WorkflowRunModelCallBodyKind.AttemptResponse : WorkflowRunModelCallBodyKind.AttemptError;
            if (!existing.Add(new BodyCaptureIdentity(candidate.ModelCallAttemptId, kind))) continue;
            var property = kind == WorkflowRunModelCallBodyKind.AttemptResponse ? "output" : "error";
            if (kind == WorkflowRunModelCallBodyKind.AttemptResponse && candidate.ResponseArtifactId is { } artifactId
                && artifactsByIdentity.TryGetValue(new ArtifactIdentity(candidate.TeamId, artifactId), out var artifact))
                _db.WorkflowRunModelCallBodyCapture.Add(AvailableCapture(candidate, kind, property, artifact, now));
            else
                _db.WorkflowRunModelCallBodyCapture.Add(PendingCapture(candidate, kind, candidate.SourceTerminalRecordId, property, now));
            declared++;
        }

        if (declared > 0) await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return declared;
    }

    private static WorkflowRunModelCallBodyCapture PendingCapture(BodyCaptureCandidate source, WorkflowRunModelCallBodyKind kind,
        Guid sourceRecordId, string sourceProperty, DateTimeOffset now) => Capture(source, kind, sourceRecordId, sourceProperty, now);

    private static WorkflowRunModelCallBodyCapture AvailableCapture(BodyCaptureCandidate source, WorkflowRunModelCallBodyKind kind,
        string sourceProperty, BodyArtifact artifact, DateTimeOffset now)
    {
        var capture = Capture(source, kind, source.SourceTerminalRecordId, sourceProperty, now);
        capture.State = WorkflowRunModelCallBodyCaptureState.Available;
        capture.ArtifactId = artifact.Id;
        capture.SourceSha256 = artifact.Sha256;
        capture.SizeBytes = artifact.SizeBytes;
        capture.ContentType = artifact.ContentType;
        capture.MaterializationFormat = WorkflowRunModelCallBodyMaterializationFormats.ExternalArtifact;
        capture.TerminalAt = now;
        return capture;
    }

    private static WorkflowRunModelCallBodyCapture Capture(BodyCaptureCandidate source, WorkflowRunModelCallBodyKind kind,
        Guid sourceRecordId, string sourceProperty, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(), TeamId = source.TeamId, WorkflowRunId = source.WorkflowRunId, ModelCallId = source.ModelCallId,
        ModelCallAttemptId = source.ModelCallAttemptId, BodyKind = kind, SourceKind = SourceKind, SourceRecordId = sourceRecordId,
        SourceProperty = sourceProperty, State = WorkflowRunModelCallBodyCaptureState.Pending, NextMaterializationAt = now,
        Revision = 1, CreatedAt = now, LastModifiedAt = now,
    };

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

    private async Task<Dictionary<SourceScope, SourceTerminal>> ReadTerminalsAsync(IReadOnlyCollection<SourceScope> scopes, CancellationToken cancellationToken)
    {
        var runIds = scopes.Select(value => value.RunId).Distinct().ToArray();
        var correlationIds = scopes.Select(value => value.CorrelationId).Distinct().ToArray();
        var candidates = await _db.WorkflowRunRecord.AsNoTracking()
            .Where(record => record.CorrelationId != null && runIds.Contains(record.RunId) && correlationIds.Contains(record.CorrelationId.Value)
                && (record.RecordType == WorkflowRunRecordTypes.InteractionCompleted || record.RecordType == WorkflowRunRecordTypes.InteractionFailed))
            .Select(record => new SourceTerminal(record.Id, record.RunId, record.NodeId, record.IterationKey, record.CorrelationId!.Value,
                record.Sequence, record.OccurredAt, record.RecordType, record.PayloadJson)).ToListAsync(cancellationToken).ConfigureAwait(false);
        var requested = scopes.ToHashSet();
        return candidates.Where(value => requested.Contains(SourceScope.For(value))).GroupBy(SourceScope.For)
            .ToDictionary(group => group.Key, group => group.OrderBy(value => value.OccurredAt).ThenBy(value => value.Id).First());
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

    private static WorkflowRunModelCall BuildStartedCall(StartedCandidate source, ParsedPayload payload) => new()
    {
        Id = Guid.NewGuid(), TeamId = source.TeamId, WorkflowRunId = source.RunId, NodeId = source.NodeId, IterationKey = source.IterationKey,
        CallOrdinal = Math.Max(1, source.Sequence), SourceKind = SourceKind, SourceCorrelationId = source.CorrelationId, Purpose = Purpose(payload.Kind),
        RequestedProvider = Limit(payload.Provider, 100), RequestedModel = Limit(payload.Model, 500), CaptureSource = SourceKind,
        CaptureCompleteness = payload.IsCorrupt ? WorkflowRunCaptureCompleteness.Corrupt : WorkflowRunCaptureCompleteness.Partial,
        SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedDate = source.OccurredAt, CreatedBy = source.ActorId,
        LastModifiedDate = source.OccurredAt, LastModifiedBy = source.ActorId,
    };

    private static WorkflowRunModelCallAttempt BuildStartedAttempt(StartedCandidate source, ParsedPayload payload, Guid callId) => new()
    {
        Id = Guid.NewGuid(), TeamId = source.TeamId, WorkflowRunId = source.RunId, ModelCallId = callId, AttemptOrdinal = 1,
        SourceStartedRecordId = source.RecordId, SourceEvidenceRevision = 1, Status = "Pending", CaptureSource = SourceKind,
        CaptureCompleteness = payload.IsCorrupt ? WorkflowRunCaptureCompleteness.Corrupt : WorkflowRunCaptureCompleteness.Partial,
        StartedAt = source.OccurredAt, SchemaVersion = WorkflowRunDataContract.CurrentVersion, CreatedDate = source.OccurredAt,
        CreatedBy = source.ActorId, LastModifiedDate = source.OccurredAt, LastModifiedBy = source.ActorId,
    };

    private static WorkflowRunModelCallAttempt BuildAttempt(ProjectionInput input, Guid callId, Guid? startedRecordId, Guid? responseArtifactId) => new()
    {
        Id = Guid.NewGuid(), TeamId = input.Candidate.TeamId, WorkflowRunId = input.Candidate.RunId, ModelCallId = callId, AttemptOrdinal = 1,
        SourceStartedRecordId = startedRecordId, SourceTerminalRecordId = input.Candidate.RecordId, SourceEvidenceRevision = 1,
        EffectiveProvider = Limit(input.TerminalPayload.Provider, 100), EffectiveModel = Limit(input.TerminalPayload.Model, 500),
        ResponseArtifactId = responseArtifactId, Status = Status(input.Candidate.RecordType, input.TerminalPayload.FailureKind),
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

    private static void ApplyTerminal(WorkflowRunModelCall call, WorkflowRunModelCallAttempt attempt, ParsedPayload startedPayload,
        SourceTerminal terminal, ParsedPayload terminalPayload, Guid? responseArtifactId)
    {
        call.CaptureCompleteness = Completeness(startedPayload, terminalPayload);
        call.LastModifiedDate = terminal.OccurredAt;
        attempt.SourceTerminalRecordId = terminal.Id;
        attempt.SourceEvidenceRevision++;
        attempt.EffectiveProvider = Limit(terminalPayload.Provider, 100);
        attempt.EffectiveModel = Limit(terminalPayload.Model, 500);
        attempt.ResponseArtifactId = responseArtifactId;
        attempt.Status = Status(terminal.RecordType, terminalPayload.FailureKind);
        attempt.ErrorCode = Limit(terminalPayload.ErrorCode, 200);
        attempt.FinishReason = Limit(terminalPayload.FinishReason, 100);
        attempt.CaptureCompleteness = Completeness(startedPayload, terminalPayload);
        attempt.InputTokens = terminalPayload.InputTokens;
        attempt.OutputTokens = terminalPayload.OutputTokens;
        attempt.CompletedAt = terminal.OccurredAt;
        attempt.LastModifiedDate = terminal.OccurredAt;
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
    private sealed record StartedCandidate(Guid RecordId, long Sequence, Guid RunId, Guid TeamId, Guid ActorId, string? NodeId,
        string IterationKey, Guid CorrelationId, DateTimeOffset OccurredAt, string PayloadJson);
    private sealed record LateStartCandidate(Guid AttemptId, int SourceEvidenceRevision, Guid TerminalRecordId, Guid ModelCallId,
        Guid TeamId, Guid WorkflowRunId, string? NodeId, string IterationKey, Guid CorrelationId);
    private sealed record LateTerminalCandidate(Guid AttemptId, int SourceEvidenceRevision, Guid ModelCallId, Guid TeamId,
        Guid WorkflowRunId, string? NodeId, string IterationKey, Guid CorrelationId);
    private sealed record OrphanedStartCandidate(Guid AttemptId, int SourceEvidenceRevision, Guid ModelCallId, Guid WorkflowRunId, DateTimeOffset TerminalAt);
    private sealed record BodyCaptureCandidate(Guid TeamId, Guid WorkflowRunId, Guid ModelCallId, Guid ModelCallAttemptId,
        Guid? SourceStartedRecordId, Guid SourceTerminalRecordId, string TerminalRecordType, Guid? ResponseArtifactId);
    private sealed record StartedBodyCaptureCandidate(Guid TeamId, Guid WorkflowRunId, Guid ModelCallId, Guid ModelCallAttemptId, Guid SourceStartedRecordId);
    private sealed record BodyArtifact(Guid TeamId, Guid Id, string Sha256, long SizeBytes, string ContentType);
    private readonly record struct BodyCaptureIdentity(Guid ModelCallAttemptId, WorkflowRunModelCallBodyKind BodyKind);
    private readonly record struct SourceIdentity(Guid TeamId, Guid RunId, Guid CorrelationId)
    {
        public static SourceIdentity For(TerminalCandidate value) => new(value.TeamId, value.RunId, value.CorrelationId);
        public static SourceIdentity For(StartedCandidate value) => new(value.TeamId, value.RunId, value.CorrelationId);
    }
    private readonly record struct SourceScope(Guid RunId, string? NodeId, string IterationKey, Guid CorrelationId)
    {
        public static SourceScope For(TerminalCandidate value) => new(value.RunId, value.NodeId, value.IterationKey, value.CorrelationId);
        public static SourceScope For(StartedCandidate value) => new(value.RunId, value.NodeId, value.IterationKey, value.CorrelationId);
        public static SourceScope For(SourceStarted value) => new(value.RunId, value.NodeId, value.IterationKey, value.CorrelationId);
        public static SourceScope For(SourceTerminal value) => new(value.RunId, value.NodeId, value.IterationKey, value.CorrelationId);
        public static SourceScope For(LateTerminalCandidate value) => new(value.WorkflowRunId, value.NodeId, value.IterationKey, value.CorrelationId);
    }
    private sealed record SourceStarted(Guid Id, Guid RunId, string? NodeId, string IterationKey, Guid CorrelationId, long Sequence,
        DateTimeOffset OccurredAt, string PayloadJson);
    private sealed record SourceTerminal(Guid Id, Guid RunId, string? NodeId, string IterationKey, Guid CorrelationId, long Sequence,
        DateTimeOffset OccurredAt, string RecordType, string PayloadJson);
    private readonly record struct ArtifactIdentity(Guid TeamId, Guid ArtifactId);
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
                var malformed = false;
                var kind = Text(root, "kind", ref malformed);
                var provider = Text(root, "provider", ref malformed);
                var model = Text(root, "model", ref malformed);
                var failureKind = Text(root, "failureKind", ref malformed);
                var category = Text(root, "category", ref malformed);
                var usage = OptionalObject(root, "usage", ref malformed);
                var finishReason = usage is { } usageValue ? Text(usageValue, "finishReason", ref malformed) : null;
                var inputTokens = usage is { } inputUsage ? NonNegativeLong(inputUsage, "inputTokens", ref malformed) : null;
                var outputTokens = usage is { } outputUsage ? NonNegativeLong(outputUsage, "outputTokens", ref malformed) : null;
                var outputArtifactId = ArtifactId(root, "output", ref malformed);
                return new ParsedPayload(malformed, kind, provider, model, failureKind, category ?? failureKind, finishReason,
                    inputTokens, outputTokens, outputArtifactId);
            }
            catch (JsonException)
            {
                return Corrupt();
            }
        }

        private static ParsedPayload Corrupt() => new(true, null, null, null, null, null, null, null, null, null);
        private static string? Text(JsonElement root, string name, ref bool malformed)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            malformed = true;
            return null;
        }

        private static JsonElement? OptionalObject(JsonElement root, string name, ref bool malformed)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind == JsonValueKind.Object) return value;
            malformed = true;
            return null;
        }

        private static long? NonNegativeLong(JsonElement root, string name, ref bool malformed)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var parsed) && parsed >= 0) return parsed;
            malformed = true;
            return null;
        }

        private static Guid? ArtifactId(JsonElement root, string name, ref bool malformed)
        {
            if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object
                || !value.TryGetProperty("$artifact_id", out var id)) return null;
            if (id.ValueKind == JsonValueKind.String && Guid.TryParse(id.GetString(), out var parsed) && parsed != Guid.Empty) return parsed;
            malformed = true;
            return null;
        }
    }
}

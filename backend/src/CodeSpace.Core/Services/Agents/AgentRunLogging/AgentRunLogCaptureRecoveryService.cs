using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace CodeSpace.Core.Services.Agents.AgentRunLogging;

/// <summary>
/// Database-fenced recovery of expected AgentRun log sources. Claims are short, provider work is outside the claim
/// transaction, and settlement proves the exact recovery owner/fence. AgentRun status/result are read-only inputs.
/// </summary>
public sealed partial class AgentRunLogCaptureRecoveryService : IAgentRunLogCaptureRecoveryService
{
    private static readonly TimeSpan MinimumLeaseMargin = TimeSpan.FromSeconds(1);
    private static readonly AgentRunLogCaptureRecoveryOptions Defaults = new(50, 8, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(5),
        new AgentRunLogCaptureRetryPolicy(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(15), 8, TimeSpan.FromHours(24), TimeSpan.FromMinutes(2)));
    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IAgentRunLogService _logs;
    private readonly AgentRunLogCaptureRecoveryOptions _options;

    public AgentRunLogCaptureRecoveryService(DbContextOptions<CodeSpaceDbContext> dbOptions, IAgentRunLogService logs) : this(dbOptions, logs, Defaults) { }

    internal AgentRunLogCaptureRecoveryService(DbContextOptions<CodeSpaceDbContext> dbOptions, IAgentRunLogService logs, AgentRunLogCaptureRecoveryOptions options)
    {
        if (options.BatchSize is <= 0 or > 500 || options.MaxConcurrency is <= 0 or > 32 || options.MaxConcurrency > options.BatchSize
            || options.LeaseDuration <= options.OperationTimeout + options.OperationTimeout + MinimumLeaseMargin || options.OperationTimeout <= TimeSpan.Zero
            || options.RetryPolicy.BaseDelay <= TimeSpan.Zero || options.RetryPolicy.MaxDelay < options.RetryPolicy.BaseDelay
            || options.RetryPolicy.MaxAttempts <= 0 || options.RetryPolicy.MaxAge <= TimeSpan.Zero || options.RetryPolicy.TerminalGrace < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options));
        _dbOptions = dbOptions;
        _logs = logs;
        _options = options;
    }

    public async Task<AgentRunLogCaptureDeclarationResult> DeclareAsync(AgentRunLogCaptureDeclarationRequest request, CancellationToken cancellationToken)
    {
        if (!Valid(request)) return new AgentRunLogCaptureDeclarationResult.Rejected(AgentRunLogCaptureDeclarationProblem.InvalidRequest);
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.AgentRun.FromSqlInterpolated($"SELECT agent_run.*, xmin FROM agent_run WHERE team_id = {request.TeamId} AND id = {request.AgentRunId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (run == null) return new AgentRunLogCaptureDeclarationResult.Rejected(AgentRunLogCaptureDeclarationProblem.MissingRun);
        if (run.Status != AgentRunStatus.Running) return new AgentRunLogCaptureDeclarationResult.Rejected(AgentRunLogCaptureDeclarationProblem.RunNotRunning);
        if (run.FenceEpoch != request.WorkerFenceEpoch) return new AgentRunLogCaptureDeclarationResult.Rejected(AgentRunLogCaptureDeclarationProblem.StaleWorker);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);

        var kinds = request.Streams.Select(value => value.StreamKind).ToArray();
        var existing = await db.AgentRunLogCaptureIntent.Where(value => value.TeamId == request.TeamId && value.AgentRunId == request.AgentRunId
                && value.WorkerFenceEpoch == request.WorkerFenceEpoch && value.CaptureSessionId == request.CaptureSessionId && kinds.Contains(value.StreamKind))
            .ToDictionaryAsync(value => value.StreamKind, cancellationToken).ConfigureAwait(false);
        foreach (var stream in request.Streams)
        {
            if (existing.TryGetValue(stream.StreamKind, out var current))
            {
                if (!Same(current, stream)) return new AgentRunLogCaptureDeclarationResult.Rejected(AgentRunLogCaptureDeclarationProblem.IdentityConflict);
                continue;
            }
            db.AgentRunLogCaptureIntent.Add(new AgentRunLogCaptureIntent
            {
                Id = Guid.NewGuid(), TeamId = request.TeamId, AgentRunId = request.AgentRunId,
                WorkerFenceEpoch = request.WorkerFenceEpoch, CaptureSessionId = request.CaptureSessionId,
                StreamKind = stream.StreamKind, ContentType = stream.ContentType, ContentEncoding = stream.ContentEncoding,
                CaptureSource = stream.CaptureSource, State = AgentRunLogCaptureIntentState.Expected, Revision = 1,
                NextRecoveryAt = now, CreatedAt = now, LastModifiedAt = now,
            });
        }
        var created = request.Streams.Count - existing.Count;
        if (created > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return new AgentRunLogCaptureDeclarationResult.Declared(created, existing.Count);
    }

    public async Task<AgentRunLogCaptureRecoverySummary> ReconcileAsync(CancellationToken cancellationToken)
    {
        var ownerId = Guid.NewGuid();
        await using var clockDb = CreateDb();
        var cutoff = await DatabaseClockAsync(clockDb, cancellationToken).ConfigureAwait(false);
        var counts = new RecoveryCounts();
        while (counts.Claimed < _options.BatchSize)
        {
            var limit = Math.Min(_options.MaxConcurrency, _options.BatchSize - counts.Claimed);
            var claims = await ClaimBatchAsync(ownerId, cutoff, limit, cancellationToken).ConfigureAwait(false);
            if (claims.Count == 0) break;
            counts.Claimed += claims.Count;
            var settlements = await Task.WhenAll(claims.Select(claim => RecoverClaimAsync(claim, cancellationToken))).ConfigureAwait(false);
            foreach (var settlement in settlements)
            {
                if (settlement.LostLease) counts.LostLease++;
                else counts.Record(settlement.State);
            }
        }
        return counts.Summary();
    }

    private async Task<RecoverySettlement> RecoverClaimAsync(RecoveryClaim claim, CancellationToken cancellationToken)
    {
        RecoveryOutcome outcome;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(_options.OperationTimeout);
        try { outcome = await RecoverAsync(claim, operation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            outcome = RecoveryOutcome.Retry(claim.StreamId, claim.State, "recovery-operation-timeout", "The bounded log recovery operation timed out.");
        }
        catch (Exception)
        {
            outcome = RecoveryOutcome.Retry(claim.StreamId, claim.State, "recovery-operation-exception", "The log recovery operation raised an unexpected error.");
        }

        using var settlement = new CancellationTokenSource(_options.OperationTimeout);
        try
        {
            return await SettleAsync(claim, outcome, settlement.Token).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested) { return new RecoverySettlement(outcome.State, true); }
    }

    private async Task<IReadOnlyList<RecoveryClaim>> ClaimBatchAsync(Guid ownerId, DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        // Every settled row becomes terminal or moves strictly beyond the frozen cutoff. Re-reading the fair queue head
        // therefore advances without OFFSET and, unlike a global cursor, cannot skip a busy tenant after another tenant's
        // later head was selected in the same wave. LIMIT belongs to the locking SELECT so SKIP LOCKED can backfill from
        // later tenants; the materialized per-tenant head prevents a locked head from being overtaken within its tenant.
        var rows = await db.AgentRunLogCaptureIntent.FromSqlInterpolated($$"""
            WITH fair AS MATERIALIZED (
                SELECT DISTINCT ON (intent.team_id) intent.id, intent.team_id, intent.next_recovery_at
                FROM agent_run_log_capture_intent intent
                JOIN agent_run run ON run.team_id = intent.team_id AND run.id = intent.agent_run_id
                WHERE intent.state IN ('Expected', 'Opened', 'SourceFinalized')
                  AND intent.next_recovery_at <= {{cutoff}}
                  AND (intent.recovery_lease_expires_at IS NULL OR intent.recovery_lease_expires_at <= clock_timestamp())
                  AND (run.status <> 'Running' OR run.fence_epoch <> intent.worker_fence_epoch)
                ORDER BY intent.team_id, intent.next_recovery_at, intent.id
            )
            SELECT intent.*, intent.xmin FROM agent_run_log_capture_intent intent
            JOIN fair ON fair.id = intent.id
            ORDER BY intent.next_recovery_at, intent.team_id, intent.id
            LIMIT {{limit}}
            FOR UPDATE OF intent SKIP LOCKED
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.RecoveryOwnerId = ownerId;
            row.RecoveryFenceEpoch++;
            row.RecoveryAttemptCount++;
            row.RecoveryStartedAt ??= now;
            row.RecoveryLeaseExpiresAt = now.Add(_options.LeaseDuration);
            row.Revision++;
            row.LastModifiedAt = now;
        }
        if (rows.Count > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(RecoveryClaim.From).ToArray();
    }

    private async Task<RecoveryOutcome> RecoverAsync(RecoveryClaim claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var run = await db.AgentRun.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == claim.TeamId && value.Id == claim.AgentRunId, cancellationToken).ConfigureAwait(false);
        if (run == null) return RecoveryOutcome.Failed(claim.StreamId, "agent-run-missing", "The capture intent lost its owning AgentRun.");
        if (run.FenceEpoch > claim.WorkerFenceEpoch) return RecoveryOutcome.Superseded(claim.StreamId, "worker-fence-superseded", "A newer AgentRun worker fence owns capture recovery.");
        if (run.FenceEpoch < claim.WorkerFenceEpoch) return RecoveryOutcome.Failed(claim.StreamId, "worker-fence-regressed", "The AgentRun worker fence regressed below the declared capture identity.");

        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        var stream = await db.AgentRunLogStream.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == claim.TeamId && value.AgentRunId == claim.AgentRunId && value.StreamKind == claim.StreamKind, cancellationToken).ConfigureAwait(false);
        if (stream == null)
        {
            if (run.Status == AgentRunStatus.Running)
                return RecoveryOutcome.Retry(null, AgentRunLogCaptureIntentState.Expected, "expected-stream-not-opened", "The declared stream has not been opened yet.");
            return AwaitTerminalGrace(claim, now, null, AgentRunLogCaptureIntentState.Expected)
                ?? RecoveryOutcome.Failed(null, "expected-stream-missing", "The terminal AgentRun never opened its declared log stream.");
        }
        if (!ExactClaim(stream, claim))
            return stream.WorkerFenceEpoch == claim.WorkerFenceEpoch
                ? RecoveryOutcome.Superseded(null, "source-finalized-before-superseded", "The exact source finalized before a later capture session replaced it.")
                : RecoveryOutcome.Superseded(null, "stream-claim-identity-mismatch", "The existing stream belongs to a different exact worker fence or capture session.");
        if (!SameContent(stream, claim)) return RecoveryOutcome.Failed(null, "stream-identity-conflict", "The opened stream does not match its declared content identity.");

        var session = await db.AgentRunLogCaptureSession.AsNoTracking().SingleOrDefaultAsync(value => value.TeamId == claim.TeamId && value.StreamId == stream.Id && value.CaptureSessionId == claim.CaptureSessionId, cancellationToken).ConfigureAwait(false);
        if (session == null) return RecoveryOutcome.Failed(stream.Id, "capture-session-ledger-missing", "The opened stream has no exact capture-session ledger row.");
        if (session.State == AgentRunLogCaptureSessionState.CaptureFailed) return RecoveryOutcome.Failed(stream.Id, session.ErrorCode ?? "capture-session-failed", session.ErrorMessage ?? "The exact capture session failed.");
        if (stream.State == AgentRunLogStreamState.Completed)
            return session.State == AgentRunLogCaptureSessionState.Finalized
                ? RecoveryOutcome.Completed(stream.Id)
                : RecoveryOutcome.Failed(stream.Id, "completed-without-final-source", "The completed stream has no exact final-drain receipt.");
        if (stream.State != AgentRunLogStreamState.Open) return RecoveryOutcome.Failed(stream.Id, stream.ErrorCode ?? "stream-terminal-non-success", "The log stream reached a non-success terminal capture state.");
        if (session.State == AgentRunLogCaptureSessionState.Open)
        {
            if (run.Status == AgentRunStatus.Running)
                return RecoveryOutcome.Retry(stream.Id, AgentRunLogCaptureIntentState.Opened, "capture-source-still-open", "The native source has not produced a final-drain receipt yet.");
            return AwaitTerminalGrace(claim, now, stream.Id, AgentRunLogCaptureIntentState.Opened)
                ?? await FailOpenStreamAsync(claim, stream, cancellationToken).ConfigureAwait(false);
        }

        if (run.Status == AgentRunStatus.Running)
            return RecoveryOutcome.Retry(stream.Id, AgentRunLogCaptureIntentState.SourceFinalized, "agent-run-still-active", "The source finalized while its AgentRun is still active.");
        return await CompleteStreamAsync(claim, stream, cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecoveryOutcome> CompleteStreamAsync(RecoveryClaim claim, AgentRunLogStream stream, CancellationToken cancellationToken)
    {
        var result = await _logs.CompleteAsync(new AgentRunLogCompleteRequest
        {
            TeamId = claim.TeamId, AgentRunId = claim.AgentRunId, StreamId = stream.Id,
            WorkerFenceEpoch = claim.WorkerFenceEpoch, CaptureSessionId = claim.CaptureSessionId,
            ExpectedRevision = stream.Revision, OperationTimeout = _options.OperationTimeout, RecoveryClaim = Fence(claim),
        }, cancellationToken).ConfigureAwait(false);
        if (result is AgentRunLogCompleteResult.Completed) return RecoveryOutcome.Completed(stream.Id);
        var problem = ((AgentRunLogCompleteResult.Rejected)result).Problem;
        if (problem.IsRetryable || problem.Code is AgentRunLogProblemCode.BackendUnavailable or AgentRunLogProblemCode.ProviderTimeout or AgentRunLogProblemCode.ConcurrentMutation)
            return RecoveryOutcome.Retry(stream.Id, AgentRunLogCaptureIntentState.SourceFinalized, $"complete-{Code(problem.Code)}", "The finalized stream could not yet be verified.");
        if (problem.Code is AgentRunLogProblemCode.StaleWorker or AgentRunLogProblemCode.StaleRecoveryClaim or AgentRunLogProblemCode.CaptureClaimConflict)
            return RecoveryOutcome.Superseded(stream.Id, $"complete-{Code(problem.Code)}", "The finalized stream lost its exact capture claim.");
        return await FailFinalizedStreamAsync(claim, stream, $"complete-{Code(problem.Code)}", cancellationToken).ConfigureAwait(false);
    }

    private async Task<RecoveryOutcome> FailOpenStreamAsync(RecoveryClaim claim, AgentRunLogStream stream, CancellationToken cancellationToken) =>
        await FailStreamAsync(claim, stream, "source-not-finalized-after-terminal", "The terminal AgentRun's native log source never produced a final-drain receipt.", cancellationToken).ConfigureAwait(false);

    private async Task<RecoveryOutcome> FailFinalizedStreamAsync(RecoveryClaim claim, AgentRunLogStream stream, string code, CancellationToken cancellationToken) =>
        await FailStreamAsync(claim, stream, code, "The finalized AgentRun log could not be verified as complete.", cancellationToken).ConfigureAwait(false);

    private async Task<RecoveryOutcome> FailStreamAsync(RecoveryClaim claim, AgentRunLogStream stream, string code, string message, CancellationToken cancellationToken)
    {
        var result = await _logs.FailCaptureAsync(new AgentRunLogFailCaptureRequest
        {
            TeamId = claim.TeamId, AgentRunId = claim.AgentRunId, StreamId = stream.Id,
            WorkerFenceEpoch = claim.WorkerFenceEpoch, CaptureSessionId = claim.CaptureSessionId,
            ExpectedRevision = stream.Revision, ErrorCode = code, ErrorMessage = message, RecoveryClaim = Fence(claim),
        }, cancellationToken).ConfigureAwait(false);
        if (result is AgentRunLogFailCaptureResult.Failed) return RecoveryOutcome.Failed(stream.Id, code, message);
        var problem = ((AgentRunLogFailCaptureResult.Rejected)result).Problem;
        return problem.Code is AgentRunLogProblemCode.StaleWorker or AgentRunLogProblemCode.StaleRecoveryClaim or AgentRunLogProblemCode.CaptureClaimConflict
            ? RecoveryOutcome.Superseded(stream.Id, $"fail-{Code(problem.Code)}", "The stream lost its exact capture claim before health settlement.")
            : RecoveryOutcome.Retry(stream.Id, claim.State, $"fail-{Code(problem.Code)}", "The stream health transition could not yet be persisted.");
    }

    private async Task<RecoverySettlement> SettleAsync(RecoveryClaim claim, RecoveryOutcome outcome, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var run = await db.AgentRun.FromSqlInterpolated($"SELECT agent_run.*, xmin FROM agent_run WHERE team_id = {claim.TeamId} AND id = {claim.AgentRunId} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var row = await db.AgentRunLogCaptureIntent.FromSqlInterpolated($"SELECT agent_run_log_capture_intent.*, xmin FROM agent_run_log_capture_intent WHERE id = {claim.Id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (run == null || row == null || row.RecoveryOwnerId != claim.RecoveryOwnerId || row.RecoveryFenceEpoch != claim.RecoveryFenceEpoch || row.RecoveryLeaseExpiresAt <= now)
            return new RecoverySettlement(outcome.State, true);

        var settled = outcome;
        if (run.FenceEpoch != row.WorkerFenceEpoch)
            settled = RecoveryOutcome.Superseded(row.StreamId, "worker-fence-changed-before-settlement", "The AgentRun worker fence changed after recovery observation and before settlement.");
        else if (!outcome.Terminal && outcome.RetryDirective is not { ArmTerminalGrace: true }
            && (row.RecoveryAttemptCount >= _options.RetryPolicy.MaxAttempts || now - row.RecoveryStartedAt!.Value >= _options.RetryPolicy.MaxAge))
            settled = RecoveryOutcome.Indeterminate(outcome.StreamId, "recovery-exhausted", $"Recovery exhausted its bounded attempts or age after '{outcome.ErrorCode}'.");

        row.StreamId = settled.StreamId ?? row.StreamId;
        row.State = settled.State;
        row.LastErrorCode = settled.ErrorCode;
        row.LastErrorMessage = settled.ErrorMessage;
        if (settled.RetryDirective is { ArmTerminalGrace: true } && row.TerminalObservedAt == null) row.TerminalObservedAt = now;
        row.NextRecoveryAt = settled.Terminal ? row.NextRecoveryAt : NextRetryAt(row, settled.RetryDirective!, now);
        row.TerminalAt = settled.Terminal ? now : null;
        row.RecoveryOwnerId = null;
        row.RecoveryLeaseExpiresAt = null;
        row.Revision++;
        row.LastModifiedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new RecoverySettlement(settled.State, false);
        }
        catch (DbUpdateConcurrencyException) { return new RecoverySettlement(settled.State, true); }
    }

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);
    private RecoveryOutcome? AwaitTerminalGrace(RecoveryClaim claim, DateTimeOffset now, Guid? streamId, AgentRunLogCaptureIntentState state)
    {
        if (claim.TerminalObservedAt == null)
            return RecoveryOutcome.Retry(streamId, state, "terminal-grace-armed", "The first terminal observation armed a DB-clock final-drain grace window.",
                new RecoveryRetryDirective(true, _options.RetryPolicy.TerminalGrace));
        var remaining = claim.TerminalObservedAt.Value.Add(_options.RetryPolicy.TerminalGrace) - now;
        return remaining > TimeSpan.Zero
            ? RecoveryOutcome.Retry(streamId, state, "terminal-grace-wait", "The DB-clock final-drain grace window remains active.", new RecoveryRetryDirective(false, remaining))
            : null;
    }

    private DateTimeOffset NextRetryAt(AgentRunLogCaptureIntent row, RecoveryRetryDirective directive, DateTimeOffset now)
    {
        if (directive.MinimumDelay is { } exactDelay) return now.Add(exactDelay);
        var exponent = Math.Min(Math.Max(row.RecoveryAttemptCount - 1, 0), 30);
        var uncappedTicks = _options.RetryPolicy.BaseDelay.Ticks * Math.Pow(2, exponent);
        var cappedTicks = Math.Min(_options.RetryPolicy.MaxDelay.Ticks, uncappedTicks);
        var bytes = row.Id.ToByteArray();
        var seed = BitConverter.ToUInt32(bytes, 0) ^ (uint)row.RecoveryAttemptCount;
        var jitter = 0.875 + seed % 2001 / 8000d;
        var delayedTicks = Math.Clamp((long)(cappedTicks * jitter), TimeSpan.TicksPerMillisecond, _options.RetryPolicy.MaxDelay.Ticks);
        return now.AddTicks(delayedTicks);
    }

    private static Task<DateTimeOffset> DatabaseClockAsync(CodeSpaceDbContext db, CancellationToken cancellationToken) =>
        db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
    private static bool Valid(AgentRunLogCaptureDeclarationRequest request) => request.TeamId != Guid.Empty && request.AgentRunId != Guid.Empty && request.WorkerFenceEpoch > 0 && request.CaptureSessionId != Guid.Empty
        && request.Streams is { Count: > 0 and <= 32 } && request.Streams.All(Valid)
        && request.Streams.Select(value => value.StreamKind).Distinct(StringComparer.Ordinal).Count() == request.Streams.Count;
    private static bool Valid(AgentRunLogExpectedStream value) => KeyPattern().IsMatch(value.StreamKind ?? "") && ContentTypePattern().IsMatch(value.ContentType ?? "")
        && (value.ContentEncoding == null || EncodingPattern().IsMatch(value.ContentEncoding)) && KeyPattern().IsMatch(value.CaptureSource ?? "");
    private static bool Same(AgentRunLogCaptureIntent value, AgentRunLogExpectedStream expected) => value.ContentType == expected.ContentType && value.ContentEncoding == expected.ContentEncoding && value.CaptureSource == expected.CaptureSource;
    private static bool ExactClaim(AgentRunLogStream value, RecoveryClaim expected) => value.TeamId == expected.TeamId && value.AgentRunId == expected.AgentRunId
        && value.WorkerFenceEpoch == expected.WorkerFenceEpoch && value.CaptureSessionId == expected.CaptureSessionId && value.StreamKind == expected.StreamKind;
    private static bool SameContent(AgentRunLogStream value, RecoveryClaim expected) => value.ContentType == expected.ContentType && value.ContentEncoding == expected.ContentEncoding && value.CaptureSource == expected.CaptureSource;
    private static string Code<T>(T value) where T : struct, Enum => string.Concat(value.ToString().Select((character, index) => char.IsUpper(character) && index > 0 ? $"-{char.ToLowerInvariant(character)}" : char.ToLowerInvariant(character).ToString()));
    private static AgentRunLogRecoveryClaimRef Fence(RecoveryClaim claim) => new(claim.Id, claim.RecoveryOwnerId, claim.RecoveryFenceEpoch);

    private sealed record RecoveryClaim(Guid Id, Guid TeamId, Guid AgentRunId, long WorkerFenceEpoch, Guid CaptureSessionId, string StreamKind,
        string ContentType, string? ContentEncoding, string CaptureSource, Guid? StreamId, AgentRunLogCaptureIntentState State,
        DateTimeOffset NextRecoveryAt, DateTimeOffset? TerminalObservedAt, Guid RecoveryOwnerId, long RecoveryFenceEpoch)
    {
        public static RecoveryClaim From(AgentRunLogCaptureIntent value) => new(value.Id, value.TeamId, value.AgentRunId, value.WorkerFenceEpoch, value.CaptureSessionId,
            value.StreamKind, value.ContentType, value.ContentEncoding, value.CaptureSource, value.StreamId, value.State, value.NextRecoveryAt, value.TerminalObservedAt, value.RecoveryOwnerId!.Value, value.RecoveryFenceEpoch);
    }

    private sealed record RecoverySettlement(AgentRunLogCaptureIntentState State, bool LostLease);

    private sealed record RecoveryOutcome(Guid? StreamId, AgentRunLogCaptureIntentState State, string? ErrorCode, string? ErrorMessage, RecoveryRetryDirective? RetryDirective)
    {
        public bool Terminal => State is AgentRunLogCaptureIntentState.Completed or AgentRunLogCaptureIntentState.CaptureFailed or AgentRunLogCaptureIntentState.Superseded or AgentRunLogCaptureIntentState.ExternalStateIndeterminate;
        public static RecoveryOutcome Retry(Guid? streamId, AgentRunLogCaptureIntentState state, string code, string message, RecoveryRetryDirective? retry = null) => new(streamId, state, code, message, retry ?? new RecoveryRetryDirective(false, null));
        public static RecoveryOutcome Completed(Guid streamId) => new(streamId, AgentRunLogCaptureIntentState.Completed, null, null, null);
        public static RecoveryOutcome Failed(Guid? streamId, string code, string message) => new(streamId, AgentRunLogCaptureIntentState.CaptureFailed, code, message, null);
        public static RecoveryOutcome Superseded(Guid? streamId, string code, string message) => new(streamId, AgentRunLogCaptureIntentState.Superseded, code, message, null);
        public static RecoveryOutcome Indeterminate(Guid? streamId, string code, string message) => new(streamId, AgentRunLogCaptureIntentState.ExternalStateIndeterminate, code, message, null);
    }

    private sealed record RecoveryRetryDirective(bool ArmTerminalGrace, TimeSpan? MinimumDelay);

    private sealed class RecoveryCounts
    {
        public int Claimed { get; set; }
        public int Completed { get; set; }
        public int CaptureFailed { get; set; }
        public int Superseded { get; set; }
        public int ExternalStateIndeterminate { get; set; }
        public int Retried { get; set; }
        public int LostLease { get; set; }

        public void Record(AgentRunLogCaptureIntentState state)
        {
            if (state == AgentRunLogCaptureIntentState.Completed) Completed++;
            else if (state == AgentRunLogCaptureIntentState.CaptureFailed) CaptureFailed++;
            else if (state == AgentRunLogCaptureIntentState.Superseded) Superseded++;
            else if (state == AgentRunLogCaptureIntentState.ExternalStateIndeterminate) ExternalStateIndeterminate++;
            else Retried++;
        }

        public AgentRunLogCaptureRecoverySummary Summary() => new(Claimed, Completed, CaptureFailed, Superseded, Retried, LostLease) { ExternalStateIndeterminate = ExternalStateIndeterminate };
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._/-]{0,126}/v[1-9][0-9]*$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();

    [GeneratedRegex("^[^\\s/]+/[^\\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ContentTypePattern();

    [GeneratedRegex("^[a-z0-9][a-z0-9._+-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EncodingPattern();
}

internal sealed record AgentRunLogCaptureRecoveryOptions(int BatchSize, int MaxConcurrency, TimeSpan LeaseDuration, TimeSpan OperationTimeout, AgentRunLogCaptureRetryPolicy RetryPolicy);
internal sealed record AgentRunLogCaptureRetryPolicy(TimeSpan BaseDelay, TimeSpan MaxDelay, int MaxAttempts, TimeSpan MaxAge, TimeSpan TerminalGrace);

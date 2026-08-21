using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

/// <summary>
/// Leased materializer for the exact source declarations admitted by the projector. Claims and settlements are short
/// database transactions; source parsing and provider-backed artifact I/O run between them, outside every transaction.
/// A failed write releases to Pending with the immutable source id intact, so retry cannot turn failure into absence.
/// </summary>
public sealed class WorkflowRunModelCallBodyMaterializer : IWorkflowRunModelCallBodyMaterializer
{
    private const int MaxBatchSize = 1000;
    private static readonly TimeSpan MinimumLeaseMargin = TimeSpan.FromSeconds(1);
    private readonly DbContextOptions<CodeSpaceDbContext> _dbOptions;
    private readonly IWorkflowRunModelCallBodyArtifactWriter _artifacts;
    private readonly ILogger<WorkflowRunModelCallBodyMaterializer> _logger;
    private readonly WorkflowRunModelCallBodyMaterializerOptions _options;

    public WorkflowRunModelCallBodyMaterializer(DbContextOptions<CodeSpaceDbContext> dbOptions,
        IWorkflowRunModelCallBodyArtifactWriter artifacts, ILogger<WorkflowRunModelCallBodyMaterializer> logger)
        : this(dbOptions, artifacts, logger, WorkflowRunModelCallBodyMaterializerOptions.Default) { }

    internal WorkflowRunModelCallBodyMaterializer(DbContextOptions<CodeSpaceDbContext> dbOptions,
        IWorkflowRunModelCallBodyArtifactWriter artifacts, ILogger<WorkflowRunModelCallBodyMaterializer> logger,
        WorkflowRunModelCallBodyMaterializerOptions options)
    {
        if (options.MaxConcurrency is <= 0 or > 32 || options.LeaseDuration <= options.OperationTimeout + MinimumLeaseMargin
            || options.OperationTimeout <= TimeSpan.Zero || options.BaseRetryDelay <= TimeSpan.Zero
            || options.MaxRetryDelay < options.BaseRetryDelay || options.MaxAttempts <= 0 || options.MaxAge <= TimeSpan.Zero
            || options.RunFilter == Guid.Empty) throw new ArgumentOutOfRangeException(nameof(options));
        _dbOptions = dbOptions;
        _artifacts = artifacts;
        _logger = logger;
        _options = options;
    }

    public async Task<WorkflowRunModelCallBodyMaterializationSummary> SweepAsync(int batchSize, CancellationToken cancellationToken)
    {
        if (batchSize <= 0 || batchSize > MaxBatchSize) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var summary = new WorkflowRunModelCallBodyMaterializationSummary();
        var ownerId = Guid.NewGuid();
        await using var clockDb = CreateDb();
        var cutoff = await DatabaseClockAsync(clockDb, cancellationToken).ConfigureAwait(false);

        while (summary.Claimed < batchSize)
        {
            var limit = Math.Min(_options.MaxConcurrency, batchSize - summary.Claimed);
            var claims = await ClaimBatchAsync(ownerId, cutoff, limit, cancellationToken).ConfigureAwait(false);
            if (claims.Count == 0) break;
            summary.Claimed += claims.Count;
            var settlements = await Task.WhenAll(claims.Select(value => MaterializeClaimAsync(value, cancellationToken))).ConfigureAwait(false);
            foreach (var settlement in settlements) Record(summary, settlement);
        }

        return summary;
    }

    private async Task<IReadOnlyList<BodyCaptureClaim>> ClaimBatchAsync(Guid ownerId, DateTimeOffset cutoff, int limit, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var runFilter = _options.RunFilter;
        var rows = await db.WorkflowRunModelCallBodyCapture.FromSqlInterpolated($$"""
            WITH fair AS MATERIALIZED (
                SELECT DISTINCT ON (intent.team_id) intent.id, intent.team_id, intent.next_materialization_at
                FROM workflow_run_model_call_body_capture intent
                WHERE intent.state = 'Pending' AND intent.next_materialization_at <= {{cutoff}}
                  AND (intent.lease_expires_at IS NULL OR intent.lease_expires_at <= clock_timestamp())
                  AND ({{runFilter}}::uuid IS NULL OR intent.workflow_run_id = {{runFilter}})
                ORDER BY intent.team_id, intent.next_materialization_at, intent.id
            )
            SELECT intent.*, intent.xmin FROM workflow_run_model_call_body_capture intent
            JOIN fair ON fair.id = intent.id
            ORDER BY intent.next_materialization_at, intent.team_id, intent.id
            LIMIT {{limit}} FOR UPDATE OF intent SKIP LOCKED
            """).ToListAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        foreach (var row in rows)
        {
            row.LeaseOwnerId = ownerId;
            row.LeaseFence++;
            row.LeaseExpiresAt = now.Add(_options.LeaseDuration);
            row.MaterializationAttemptCount++;
            row.Revision++;
            row.LastModifiedAt = now;
        }
        if (rows.Count > 0) await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return rows.Select(BodyCaptureClaim.From).ToArray();
    }

    private async Task<BodyCaptureSettlement> MaterializeClaimAsync(BodyCaptureClaim claim, CancellationToken cancellationToken)
    {
        MaterializationOutcome outcome;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operation.CancelAfter(_options.OperationTimeout);
        try { outcome = await MaterializeAsync(claim, operation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            outcome = MaterializationOutcome.Retry("artifact-store-timeout", "The bounded body materialization operation timed out.", WorkflowRunModelCallBodyCaptureState.CaptureFailed);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Workflow Run model-call body capture {CaptureId} could not materialize.", claim.Id);
            outcome = MaterializationOutcome.Retry("artifact-store-failed", "The configured artifact store could not persist this body.", WorkflowRunModelCallBodyCaptureState.CaptureFailed);
        }

        using var settlement = new CancellationTokenSource(_options.OperationTimeout);
        try { return await SettleAsync(claim, outcome, settlement.Token).ConfigureAwait(false); }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Workflow Run model-call body capture {CaptureId} lost settlement.", claim.Id);
            return BodyCaptureSettlement.Lost();
        }
    }

    private async Task<MaterializationOutcome> MaterializeAsync(BodyCaptureClaim claim, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        var source = await db.WorkflowRunRecord.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == claim.SourceRecordId && value.RunId == claim.WorkflowRunId, cancellationToken)
            .ConfigureAwait(false);
        if (source is null) return MaterializationOutcome.Corrupt("source-record-missing", "The admitted source record is unavailable.");

        JsonElement value;
        try
        {
            using var document = JsonDocument.Parse(source.PayloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return MaterializationOutcome.Corrupt("source-payload-invalid", "The source payload is not a JSON object.");
            if (!document.RootElement.TryGetProperty(claim.SourceProperty, out var field)
                || field.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                return MaterializationOutcome.NotRecorded();
            value = field.Clone();
        }
        catch (JsonException)
        {
            return MaterializationOutcome.Corrupt("source-payload-invalid", "The source payload is not valid JSON.");
        }

        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty("$artifact_id", out var idValue))
        {
            if (idValue.ValueKind != JsonValueKind.String || !Guid.TryParse(idValue.GetString(), out var artifactId) || artifactId == Guid.Empty)
                return MaterializationOutcome.Corrupt("source-artifact-id-invalid", "The source artifact identity is malformed.");
            var referenced = await _artifacts.ReadMetadataAsync(claim.TeamId, artifactId, cancellationToken).ConfigureAwait(false);
            return referenced is null
                ? MaterializationOutcome.Retry("source-artifact-unavailable", "The referenced artifact is not available in the exact team.", WorkflowRunModelCallBodyCaptureState.ExternalStateIndeterminate)
                : MaterializationOutcome.Available(referenced, WorkflowRunModelCallBodyMaterializationFormats.ExternalArtifact);
        }

        var payload = Encode(value);
        var metadata = await _artifacts.PutAsync(claim.TeamId, payload.Bytes, WorkflowRunModelCallBodyMaterializationFormats.EnvelopeContentType, cancellationToken).ConfigureAwait(false);
        return MaterializationOutcome.Available(metadata, payload.Format);
    }

    private async Task<BodyCaptureSettlement> SettleAsync(BodyCaptureClaim claim, MaterializationOutcome outcome, CancellationToken cancellationToken)
    {
        await using var db = CreateDb();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlInterpolatedAsync($$"""
            SELECT set_config('codespace.workflow_run_model_call_body_lease_owner', {{claim.OwnerId.ToString()}}, true),
                   set_config('codespace.workflow_run_model_call_body_lease_fence', {{claim.Fence.ToString()}}, true)
            """, cancellationToken).ConfigureAwait(false);
        var row = await db.WorkflowRunModelCallBodyCapture.FromSqlInterpolated($"SELECT workflow_run_model_call_body_capture.*, xmin FROM workflow_run_model_call_body_capture WHERE id = {claim.Id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var now = await DatabaseClockAsync(db, cancellationToken).ConfigureAwait(false);
        if (row is null || row.LeaseOwnerId != claim.OwnerId || row.LeaseFence != claim.Fence || row.LeaseExpiresAt <= now)
            return BodyCaptureSettlement.Lost();

        var settled = outcome;
        if (outcome.RetryScheduled && (row.MaterializationAttemptCount >= _options.MaxAttempts || now - row.CreatedAt >= _options.MaxAge))
            settled = MaterializationOutcome.Terminal(outcome.ExhaustedState, "materialization-exhausted", "Body materialization exhausted its bounded retry attempts or age.");
        if (settled.State == WorkflowRunModelCallBodyCaptureState.Available
            && !await SetTargetReferenceAsync(db, row, settled.Artifact!.Id, now, cancellationToken).ConfigureAwait(false))
            settled = MaterializationOutcome.Terminal(WorkflowRunModelCallBodyCaptureState.ExternalStateIndeterminate,
                "target-artifact-conflict", "The model-call body target already references a different artifact.");

        row.State = settled.State;
        row.ArtifactId = settled.State == WorkflowRunModelCallBodyCaptureState.Available ? settled.Artifact!.Id : null;
        row.SourceSha256 = settled.State == WorkflowRunModelCallBodyCaptureState.Available ? settled.Artifact!.Sha256 : null;
        row.SizeBytes = settled.State == WorkflowRunModelCallBodyCaptureState.Available ? settled.Artifact!.SizeBytes : null;
        row.ContentType = settled.State == WorkflowRunModelCallBodyCaptureState.Available ? settled.Artifact!.ContentType : null;
        row.MaterializationFormat = settled.State == WorkflowRunModelCallBodyCaptureState.Available ? settled.Format : null;
        row.LastErrorCode = settled.ErrorCode;
        row.LastErrorMessage = settled.ErrorMessage;
        row.NextMaterializationAt = settled.RetryScheduled ? NextRetryAt(row, now) : row.NextMaterializationAt;
        row.TerminalAt = settled.RetryScheduled ? null : now;
        row.LeaseOwnerId = null;
        row.LeaseExpiresAt = null;
        row.Revision++;
        row.LastModifiedAt = now;
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return BodyCaptureSettlement.Settled(settled.State, settled.RetryScheduled);
        }
        catch (DbUpdateConcurrencyException) { return BodyCaptureSettlement.Lost(); }
    }

    private static async Task<bool> SetTargetReferenceAsync(CodeSpaceDbContext db, WorkflowRunModelCallBodyCapture row,
        Guid artifactId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (row.BodyKind == WorkflowRunModelCallBodyKind.LogicalRequest)
            return await db.WorkflowRunModelCall.Where(value => value.Id == row.ModelCallId && value.TeamId == row.TeamId
                    && value.WorkflowRunId == row.WorkflowRunId && (value.RequestArtifactId == null || value.RequestArtifactId == artifactId))
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.RequestArtifactId, artifactId)
                    .SetProperty(value => value.LastModifiedDate, now), cancellationToken).ConfigureAwait(false) == 1;

        var attempts = db.WorkflowRunModelCallAttempt.Where(value => value.Id == row.ModelCallAttemptId && value.ModelCallId == row.ModelCallId
            && value.TeamId == row.TeamId && value.WorkflowRunId == row.WorkflowRunId);
        return row.BodyKind switch
        {
            WorkflowRunModelCallBodyKind.AttemptResponse => await attempts.Where(value => value.ResponseArtifactId == null || value.ResponseArtifactId == artifactId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ResponseArtifactId, artifactId)
                    .SetProperty(value => value.LastModifiedDate, now), cancellationToken).ConfigureAwait(false) == 1,
            WorkflowRunModelCallBodyKind.AttemptError => await attempts.Where(value => value.ErrorArtifactId == null || value.ErrorArtifactId == artifactId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(value => value.ErrorArtifactId, artifactId)
                    .SetProperty(value => value.LastModifiedDate, now), cancellationToken).ConfigureAwait(false) == 1,
            _ => false,
        };
    }

    private DateTimeOffset NextRetryAt(WorkflowRunModelCallBodyCapture row, DateTimeOffset now)
    {
        var exponent = Math.Min(row.MaterializationAttemptCount - 1, 20);
        var rawTicks = _options.BaseRetryDelay.Ticks * Math.Pow(2, exponent);
        var cappedTicks = Math.Min(rawTicks, _options.MaxRetryDelay.Ticks);
        var jitter = 0.9 + (BitConverter.ToUInt32(row.Id.ToByteArray(), 0) % 2001) / 10000d;
        return now.AddTicks((long)(cappedTicks * jitter));
    }

    private static EncodedBody Encode(JsonElement value)
    {
        var format = value.ValueKind == JsonValueKind.String
            ? WorkflowRunModelCallBodyMaterializationFormats.Utf8StringEnvelope
            : WorkflowRunModelCallBodyMaterializationFormats.JsonEnvelope;
        var body = Encoding.UTF8.GetBytes(value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        var header = WorkflowRunModelCallBodyMaterializationFormats.Header(format);
        var bytes = new byte[header.Length + body.Length];
        header.CopyTo(bytes);
        body.CopyTo(bytes.AsSpan(header.Length));
        return new EncodedBody(bytes, format);
    }

    private CodeSpaceDbContext CreateDb() => new(_dbOptions);

    private static async Task<DateTimeOffset> DatabaseClockAsync(CodeSpaceDbContext db, CancellationToken cancellationToken) =>
        await db.Database.SqlQuery<DateTimeOffset>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(cancellationToken).ConfigureAwait(false);

    private static void Record(WorkflowRunModelCallBodyMaterializationSummary summary, BodyCaptureSettlement settlement)
    {
        if (settlement.LostLease) { summary.LostLease++; return; }
        if (settlement.RetryScheduled) { summary.RetryScheduled++; return; }
        switch (settlement.State)
        {
            case WorkflowRunModelCallBodyCaptureState.Available: summary.Available++; break;
            case WorkflowRunModelCallBodyCaptureState.NotRecorded: summary.NotRecorded++; break;
            case WorkflowRunModelCallBodyCaptureState.Corrupt: summary.Corrupt++; break;
            case WorkflowRunModelCallBodyCaptureState.CaptureFailed: summary.CaptureFailed++; break;
            case WorkflowRunModelCallBodyCaptureState.ExternalStateIndeterminate: summary.ExternalStateIndeterminate++; break;
        }
    }

    private sealed record BodyCaptureClaim
    {
        public required Guid Id { get; init; }
        public required Guid TeamId { get; init; }
        public required Guid WorkflowRunId { get; init; }
        public required Guid SourceRecordId { get; init; }
        public required string SourceProperty { get; init; }
        public required Guid OwnerId { get; init; }
        public required long Fence { get; init; }

        public static BodyCaptureClaim From(WorkflowRunModelCallBodyCapture value) => new()
        {
            Id = value.Id,
            TeamId = value.TeamId,
            WorkflowRunId = value.WorkflowRunId,
            SourceRecordId = value.SourceRecordId,
            SourceProperty = value.SourceProperty,
            OwnerId = value.LeaseOwnerId!.Value,
            Fence = value.LeaseFence,
        };
    }

    private sealed record BodyCaptureSettlement(WorkflowRunModelCallBodyCaptureState State, bool RetryScheduled, bool LostLease)
    {
        public static BodyCaptureSettlement Settled(WorkflowRunModelCallBodyCaptureState state, bool retry) => new(state, retry, false);
        public static BodyCaptureSettlement Lost() => new(WorkflowRunModelCallBodyCaptureState.Pending, false, true);
    }

    private sealed record MaterializationOutcome
    {
        public required WorkflowRunModelCallBodyCaptureState State { get; init; }
        public ArtifactMetadata? Artifact { get; init; }
        public string? Format { get; init; }
        public string? ErrorCode { get; init; }
        public string? ErrorMessage { get; init; }
        public bool RetryScheduled { get; init; }
        public WorkflowRunModelCallBodyCaptureState ExhaustedState { get; init; }

        public static MaterializationOutcome Available(ArtifactMetadata artifact, string format) => new()
        {
            State = WorkflowRunModelCallBodyCaptureState.Available,
            Artifact = artifact,
            Format = format,
        };

        public static MaterializationOutcome NotRecorded() => new() { State = WorkflowRunModelCallBodyCaptureState.NotRecorded };
        public static MaterializationOutcome Corrupt(string code, string message) => Terminal(WorkflowRunModelCallBodyCaptureState.Corrupt, code, message);
        public static MaterializationOutcome Retry(string code, string message, WorkflowRunModelCallBodyCaptureState exhaustedState) => new()
        {
            State = WorkflowRunModelCallBodyCaptureState.Pending,
            ErrorCode = code,
            ErrorMessage = message,
            RetryScheduled = true,
            ExhaustedState = exhaustedState,
        };

        public static MaterializationOutcome Terminal(WorkflowRunModelCallBodyCaptureState state, string code, string message) => new()
        {
            State = state,
            ErrorCode = code,
            ErrorMessage = message,
        };
    }

    private sealed record EncodedBody(byte[] Bytes, string Format);
}

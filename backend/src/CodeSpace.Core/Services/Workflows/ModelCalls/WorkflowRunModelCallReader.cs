using System.Buffers;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.DependencyInjection;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows.ModelCalls;
using Microsoft.EntityFrameworkCore;

namespace CodeSpace.Core.Services.Workflows.ModelCalls;

/// <summary>
/// Metadata-first, bounded reader for Workflow Run model calls. Stable-id reads use the first-class logical-call and
/// physical-attempt projection without touching body artifacts; sequence reads remain an explicit compatibility view
/// over the legacy interaction ledger. This service never changes model, workflow, completion, or terminal behavior.
/// </summary>
public sealed class WorkflowRunModelCallReader : IWorkflowRunModelCallReader, IScopedDependency
{
    public const int DefaultPageBytes = 64 * 1024;
    public const int MaxPageBytes = 256 * 1024;
    private const int MinPageBytes = 256;
    private const int Utf8LookaheadBytes = 4;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly CodeSpaceDbContext _db;
    private readonly IArtifactRangeReader _artifacts;

    public WorkflowRunModelCallReader(CodeSpaceDbContext db, IArtifactRangeReader artifacts)
    {
        _db = db;
        _artifacts = artifacts;
    }

    public async Task<WorkflowRunModelCallDetailMetadata?> ReadByIdAsync(Guid runId, Guid modelCallId, Guid teamId, CancellationToken cancellationToken)
    {
        var call = await _db.WorkflowRunModelCall.AsNoTracking()
            .SingleOrDefaultAsync(value => value.Id == modelCallId && value.WorkflowRunId == runId && value.TeamId == teamId, cancellationToken)
            .ConfigureAwait(false);
        if (call is null) return null;

        var attempts = await _db.WorkflowRunModelCallAttempt.AsNoTracking()
            .Where(value => value.ModelCallId == modelCallId && value.WorkflowRunId == runId && value.TeamId == teamId)
            .OrderBy(value => value.AttemptOrdinal)
            .ThenBy(value => value.Id)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return Detail(call, attempts);
    }

    public async Task<WorkflowRunModelCallBodyPage?> ReadBodyAsync(WorkflowRunModelCallBodyReadRequest request, CancellationToken cancellationToken)
    {
        var call = await _db.WorkflowRunModelCall.AsNoTracking()
            .Where(value => value.Id == request.ModelCallId && value.WorkflowRunId == request.RunId && value.TeamId == request.TeamId)
            .Select(value => new BodyRow(value.RequestArtifactId, value.CaptureCompleteness))
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (call is null) return null;
        if (!Enum.IsDefined(request.Body))
            return BodyUnavailable(request, WorkflowRunModelCallPartAvailability.InvalidBodyReference, "The model-call body kind is invalid.", call);

        BodyRow source;
        if (request.Body == WorkflowRunModelCallBody.LogicalRequest)
        {
            if (request.AttemptId is not null) return BodyUnavailable(request, WorkflowRunModelCallPartAvailability.InvalidBodyReference, "A logical request body cannot be scoped to a physical attempt.", call);
            source = call;
        }
        else
        {
            if (request.AttemptId is not { } attemptId)
                return BodyUnavailable(request, WorkflowRunModelCallPartAvailability.InvalidBodyReference, "A physical attempt body requires an attempt id.", call);

            var attempt = await _db.WorkflowRunModelCallAttempt.AsNoTracking()
                .Where(value => value.Id == attemptId && value.ModelCallId == request.ModelCallId
                    && value.WorkflowRunId == request.RunId && value.TeamId == request.TeamId)
                .Select(value => new AttemptBodyRow(value.RequestArtifactId, value.ResponseArtifactId, value.ErrorArtifactId, value.CaptureCompleteness))
                .SingleOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (attempt is null) return null;
            source = new BodyRow(attempt.Artifact(request.Body), attempt.CaptureCompleteness);
        }

        if (request.OffsetBytes < 0)
            return BodyUnavailable(request, WorkflowRunModelCallPartAvailability.InvalidOffset, "The byte offset is invalid.", source);
        if (source.ArtifactId is null)
            return BodyUnavailable(request, MissingAvailability(source.CaptureCompleteness), MissingMessage(source.CaptureCompleteness), source);

        var limit = Math.Clamp(request.LimitBytes, MinPageBytes, MaxPageBytes);
        var read = await _artifacts.ReadRangeAsync(request.TeamId, source.ArtifactId.Value, request.OffsetBytes, limit + Utf8LookaheadBytes, cancellationToken).ConfigureAwait(false);
        if (read.State != ArtifactRangeReadState.Available)
            return BodyUnavailable(request, Map(read.State), Message(read.State), source, new BodyMetadata(read.TotalLength, read.ContentType));

        return PageBodyUtf8(request, new BodyContent(source, read.Bytes!, read.TotalLength!.Value, read.ContentType!, read.IntegrityVerified), limit);
    }

    public async Task<WorkflowRunModelCallMetadata?> ReadMetadataAsync(Guid runId, long sequence, Guid teamId, CancellationToken cancellationToken)
    {
        var call = await FindAsync(runId, sequence, teamId, cancellationToken).ConfigureAwait(false);
        if (call is null) return null;

        var projection = await (from attempt in _db.WorkflowRunModelCallAttempt.AsNoTracking()
                                join modelCall in _db.WorkflowRunModelCall.AsNoTracking() on attempt.ModelCallId equals modelCall.Id
                                where attempt.SourceTerminalRecordId == call.Completed.Id
                                      && attempt.WorkflowRunId == runId && attempt.TeamId == teamId
                                      && modelCall.WorkflowRunId == runId && modelCall.TeamId == teamId
                                select new { modelCall.Id, modelCall.CaptureCompleteness })
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        return new WorkflowRunModelCallMetadata
        {
            RunId = runId,
            Sequence = sequence,
            WorkflowRunModelCallId = projection?.Id,
            ProjectionState = projection is null ? WorkflowRunModelCallProjectionState.LegacyFallback : WorkflowRunModelCallProjectionState.Projected,
            CaptureCompleteness = projection?.CaptureCompleteness ?? WorkflowRunCaptureCompleteness.LegacyUnknown,
            CorrelationId = call.Completed.CorrelationId,
            Status = call.Completed.RecordType == WorkflowRunRecordTypes.InteractionFailed ? WorkflowRunModelCallStatus.Failed : WorkflowRunModelCallStatus.Completed,
            Parts = Enum.GetValues<WorkflowRunModelCallPart>().Select(part => SourceFor(call, part).Descriptor(part)).ToList(),
        };
    }

    private static WorkflowRunModelCallDetailMetadata Detail(WorkflowRunModelCall call, IReadOnlyList<WorkflowRunModelCallAttempt> attempts) => new()
    {
        WorkflowRunModelCallId = call.Id,
        RunId = call.WorkflowRunId,
        CallOrdinal = call.CallOrdinal,
        NodeId = call.NodeId,
        IterationKey = call.IterationKey,
        WorkPlanId = call.WorkPlanId,
        PlanVersion = call.PlanVersion,
        WorkUnitId = call.WorkUnitId,
        WorkUnitContractHash = call.WorkUnitContractHash,
        ExecutionAttemptId = call.ExecutionAttemptId,
        ExecutionAttemptOrdinal = call.ExecutionAttemptOrdinal,
        ExecutionGeneration = call.ExecutionGeneration,
        Purpose = call.Purpose,
        RequestedProvider = call.RequestedProvider,
        RequestedModel = call.RequestedModel,
        RequestedModelRowId = call.RequestedModelRowId,
        SelectionPolicy = call.SelectionPolicy,
        SourceKind = call.SourceKind,
        SourceCorrelationId = call.SourceCorrelationId,
        CaptureSource = call.CaptureSource,
        CaptureCompleteness = call.CaptureCompleteness,
        SchemaVersion = call.SchemaVersion,
        CreatedAt = call.CreatedDate,
        Bodies = [Descriptor(WorkflowRunModelCallBody.LogicalRequest, null, call.RequestArtifactId, call.CaptureCompleteness)],
        Attempts = attempts.Select(Attempt).ToList(),
    };

    private static WorkflowRunModelCallAttemptMetadata Attempt(WorkflowRunModelCallAttempt attempt) => new()
    {
        AttemptId = attempt.Id,
        AttemptOrdinal = attempt.AttemptOrdinal,
        EffectiveProvider = attempt.EffectiveProvider,
        EffectiveModel = attempt.EffectiveModel,
        EffectiveModelRowId = attempt.EffectiveModelRowId,
        TransportKind = attempt.TransportKind,
        EndpointFingerprint = attempt.EndpointFingerprint,
        ProviderRequestId = attempt.ProviderRequestId,
        Status = attempt.Status,
        ErrorCode = attempt.ErrorCode,
        FinishReason = attempt.FinishReason,
        HttpStatusCode = attempt.HttpStatusCode,
        CaptureSource = attempt.CaptureSource,
        CaptureCompleteness = attempt.CaptureCompleteness,
        SourceEvidence = Evidence(attempt),
        SourceStartedRecordId = attempt.SourceStartedRecordId,
        SourceTerminalRecordId = attempt.SourceTerminalRecordId,
        SourceEvidenceRevision = attempt.SourceEvidenceRevision,
        Usage = new WorkflowRunModelCallUsageMetadata
        {
            InputTokens = attempt.InputTokens,
            OutputTokens = attempt.OutputTokens,
            CacheReadTokens = attempt.CacheReadTokens,
            CacheWriteTokens = attempt.CacheWriteTokens,
            ReasoningTokens = attempt.ReasoningTokens,
        },
        CostAmount = attempt.CostAmount,
        CostCurrency = attempt.CostCurrency,
        PricingVersion = attempt.PricingVersion,
        StartedAt = attempt.StartedAt,
        FirstTokenAt = attempt.FirstTokenAt,
        CompletedAt = attempt.CompletedAt,
        SchemaVersion = attempt.SchemaVersion,
        Bodies =
        [
            Descriptor(WorkflowRunModelCallBody.AttemptRequest, attempt.Id, attempt.RequestArtifactId, attempt.CaptureCompleteness),
            Descriptor(WorkflowRunModelCallBody.AttemptResponse, attempt.Id, attempt.ResponseArtifactId, attempt.CaptureCompleteness),
            Descriptor(WorkflowRunModelCallBody.AttemptError, attempt.Id, attempt.ErrorArtifactId, attempt.CaptureCompleteness),
        ],
    };

    private static WorkflowRunModelCallBodyDescriptor Descriptor(WorkflowRunModelCallBody body, Guid? attemptId, Guid? artifactId,
        WorkflowRunCaptureCompleteness completeness) => new()
    {
        Body = body,
        AttemptId = attemptId,
        ArtifactId = artifactId,
        ReferenceState = ReferenceState(artifactId, completeness),
        CaptureCompleteness = completeness,
    };

    private static WorkflowRunModelCallSourceEvidence Evidence(WorkflowRunModelCallAttempt attempt)
    {
        if (attempt.SourceTerminalRecordId is null) return WorkflowRunModelCallSourceEvidence.Native;
        if (attempt.SourceStartedRecordId is null) return WorkflowRunModelCallSourceEvidence.TerminalOnly;
        return attempt.SourceEvidenceRevision > 1
            ? WorkflowRunModelCallSourceEvidence.LateStartAttached
            : WorkflowRunModelCallSourceEvidence.StartedAndTerminal;
    }

    private static WorkflowRunModelCallBodyReferenceState ReferenceState(Guid? artifactId, WorkflowRunCaptureCompleteness completeness)
    {
        if (artifactId is not null) return WorkflowRunModelCallBodyReferenceState.Referenced;
        return completeness switch
        {
            WorkflowRunCaptureCompleteness.Exact => WorkflowRunModelCallBodyReferenceState.NotRecorded,
            WorkflowRunCaptureCompleteness.RedactedExact => WorkflowRunModelCallBodyReferenceState.Redacted,
            WorkflowRunCaptureCompleteness.Partial => WorkflowRunModelCallBodyReferenceState.Partial,
            WorkflowRunCaptureCompleteness.Unavailable => WorkflowRunModelCallBodyReferenceState.Unavailable,
            WorkflowRunCaptureCompleteness.Corrupt => WorkflowRunModelCallBodyReferenceState.Corrupt,
            _ => WorkflowRunModelCallBodyReferenceState.LegacyUnknown,
        };
    }

    private static WorkflowRunModelCallPartAvailability MissingAvailability(WorkflowRunCaptureCompleteness completeness) => completeness switch
    {
        WorkflowRunCaptureCompleteness.Exact => WorkflowRunModelCallPartAvailability.NotRecorded,
        WorkflowRunCaptureCompleteness.RedactedExact => WorkflowRunModelCallPartAvailability.Redacted,
        WorkflowRunCaptureCompleteness.Partial => WorkflowRunModelCallPartAvailability.CapturePartial,
        WorkflowRunCaptureCompleteness.Unavailable => WorkflowRunModelCallPartAvailability.CaptureUnavailable,
        WorkflowRunCaptureCompleteness.Corrupt => WorkflowRunModelCallPartAvailability.CaptureCorrupt,
        _ => WorkflowRunModelCallPartAvailability.LegacyUnknown,
    };

    private static string MissingMessage(WorkflowRunCaptureCompleteness completeness) => completeness switch
    {
        WorkflowRunCaptureCompleteness.Exact => "This body was not recorded.",
        WorkflowRunCaptureCompleteness.RedactedExact => "This body was intentionally redacted.",
        WorkflowRunCaptureCompleteness.Partial => "The capture is partial and contains no body reference.",
        WorkflowRunCaptureCompleteness.Unavailable => "The body capture is unavailable.",
        WorkflowRunCaptureCompleteness.Corrupt => "The captured body reference is corrupt or unstatable.",
        _ => "Legacy capture did not establish a body reference.",
    };

    public async Task<WorkflowRunModelCallPartPage?> ReadPartAsync(WorkflowRunModelCallPartReadRequest request, CancellationToken cancellationToken)
    {
        var call = await FindAsync(request.RunId, request.Sequence, request.TeamId, cancellationToken).ConfigureAwait(false);
        if (call is null) return null;

        var source = SourceFor(call, request.Part);
        if (source.Kind == WorkflowRunModelCallPartSource.NotRecorded)
            return Unavailable(request, WorkflowRunModelCallPartAvailability.NotRecorded, "This part was not recorded.", source);
        if (request.OffsetBytes < 0)
            return Unavailable(request, WorkflowRunModelCallPartAvailability.InvalidOffset, "The byte offset is invalid.", source);

        var limit = Math.Clamp(request.LimitBytes, MinPageBytes, MaxPageBytes);
        if (source.InlineText is { } inline)
            return PageInline(request, source, inline, limit);

        var read = await _artifacts.ReadRangeAsync(request.TeamId, source.ArtifactId!.Value, request.OffsetBytes, limit + Utf8LookaheadBytes, cancellationToken).ConfigureAwait(false);
        if (read.State != ArtifactRangeReadState.Available)
            return Unavailable(request, Map(read.State), Message(read.State), source, read.TotalLength);

        return PageUtf8(request, source, read.Bytes!, read.TotalLength!.Value, limit, read.IntegrityVerified);
    }

    private async Task<CallRecords?> FindAsync(Guid runId, long sequence, Guid teamId, CancellationToken cancellationToken)
    {
        var completed = await _db.WorkflowRunRecord.AsNoTracking()
            .Where(r => r.RunId == runId && r.Sequence == sequence && r.Run.TeamId == teamId)
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);
        if (completed is null || completed.RecordType is not (WorkflowRunRecordTypes.InteractionCompleted or WorkflowRunRecordTypes.InteractionFailed)) return null;

        var started = completed.CorrelationId is not { } correlationId ? null : await _db.WorkflowRunRecord.AsNoTracking()
            .FirstOrDefaultAsync(r => r.RunId == runId && r.CorrelationId == correlationId && r.RecordType == WorkflowRunRecordTypes.InteractionStarted, cancellationToken)
            .ConfigureAwait(false);
        return new CallRecords(started, completed);
    }

    private static PartSource SourceFor(CallRecords call, WorkflowRunModelCallPart part) => part switch
    {
        WorkflowRunModelCallPart.Result => FieldSource(call.Completed.PayloadJson, "output"),
        WorkflowRunModelCallPart.SystemPrompt => PromptSource(call.Started?.PayloadJson, "system"),
        WorkflowRunModelCallPart.UserPrompt => PromptSource(call.Started?.PayloadJson, "user"),
        WorkflowRunModelCallPart.Usage => TextSource(PrettyField(call.Completed.PayloadJson, "usage"), "application/json", WorkflowRunModelCallPartSource.Synthesized),
        WorkflowRunModelCallPart.Trace => TextSource(BuildTrace(call.Started, call.Completed), "application/json", WorkflowRunModelCallPartSource.Synthesized),
        _ => PartSource.NotRecorded,
    };

    private static PartSource PromptSource(string? payloadJson, string field)
    {
        if (payloadJson is null || !TryGetField(payloadJson, "prompt", out var prompt)) return PartSource.NotRecorded;
        if (prompt.ValueKind == JsonValueKind.Object && (prompt.TryGetProperty("system", out _) || prompt.TryGetProperty("user", out _)))
            return prompt.TryGetProperty(field, out var value) ? ElementSource(value) : PartSource.NotRecorded;

        return field == "user" ? ElementSource(prompt) : PartSource.NotRecorded;
    }

    private static PartSource FieldSource(string payloadJson, string field) => TryGetField(payloadJson, field, out var value) ? ElementSource(value) : PartSource.NotRecorded;

    private static PartSource ElementSource(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return TextSource(element.GetString(), "text/plain", WorkflowRunModelCallPartSource.Inline);
        if (element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return PartSource.NotRecorded;
        if (element.ValueKind == JsonValueKind.Object && element.TryGetProperty("$artifact_id", out var idElement)
            && idElement.ValueKind == JsonValueKind.String && Guid.TryParse(idElement.GetString(), out var artifactId))
        {
            long? size = element.TryGetProperty("size_bytes", out var sizeElement) && sizeElement.TryGetInt64(out var parsedSize) && parsedSize >= 0 ? parsedSize : null;
            var contentType = element.TryGetProperty("content_type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String ? typeElement.GetString() : null;
            return new PartSource(WorkflowRunModelCallPartSource.Artifact, null, artifactId, size, contentType ?? "application/octet-stream");
        }

        return TextSource(JsonSerializer.Serialize(element, Pretty), "application/json", WorkflowRunModelCallPartSource.Inline);
    }

    private static PartSource TextSource(string? text, string contentType, WorkflowRunModelCallPartSource kind)
    {
        if (text is null) return PartSource.NotRecorded;
        return new PartSource(kind, text, null, Encoding.UTF8.GetByteCount(text), contentType);
    }

    private static WorkflowRunModelCallPartPage PageInline(WorkflowRunModelCallPartReadRequest request, PartSource source, string text, int limit)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (request.OffsetBytes > bytes.LongLength)
            return Unavailable(request, WorkflowRunModelCallPartAvailability.InvalidOffset, "The byte offset exceeds this part's length.", source, bytes.LongLength);

        var available = Math.Min((long)limit + Utf8LookaheadBytes, bytes.LongLength - request.OffsetBytes);
        var page = bytes.AsSpan((int)request.OffsetBytes, (int)available).ToArray();
        return PageUtf8(request, source, page, bytes.LongLength, limit, integrityVerified: true);
    }

    private static WorkflowRunModelCallPartPage PageUtf8(WorkflowRunModelCallPartReadRequest request, PartSource source, byte[] bytes, long totalBytes, int limit, bool integrityVerified)
    {
        if (request.OffsetBytes > totalBytes)
            return Unavailable(request, WorkflowRunModelCallPartAvailability.InvalidOffset, "The byte offset exceeds this part's length.", source, totalBytes);
        if (bytes.Length > 0 && IsContinuation(bytes[0]))
            return Unavailable(request, WorkflowRunModelCallPartAvailability.InvalidOffset, "The byte offset is not a UTF-8 character boundary.", source, totalBytes);

        var consumed = 0;
        var max = Math.Min(limit, bytes.Length);
        while (consumed < max)
        {
            var status = Rune.DecodeFromUtf8(bytes.AsSpan(consumed), out _, out var runeBytes);
            if (status == OperationStatus.Done && consumed + runeBytes <= max)
            {
                consumed += runeBytes;
                continue;
            }
            if (status == OperationStatus.NeedMoreData || status == OperationStatus.Done) break;
            return Unavailable(request, WorkflowRunModelCallPartAvailability.IntegrityFailure, "The stored part is not valid UTF-8 text.", source, totalBytes);
        }

        if (consumed == 0 && request.OffsetBytes < totalBytes)
            return Unavailable(request, WorkflowRunModelCallPartAvailability.IntegrityFailure, "The stored part could not produce a complete UTF-8 character.", source, totalBytes);

        var next = request.OffsetBytes + consumed;
        return new WorkflowRunModelCallPartPage
        {
            Part = request.Part,
            Availability = WorkflowRunModelCallPartAvailability.Available,
            Text = Encoding.UTF8.GetString(bytes, 0, consumed),
            OffsetBytes = request.OffsetBytes,
            ReturnedBytes = consumed,
            TotalBytes = totalBytes,
            NextOffsetBytes = next < totalBytes ? next : null,
            ContentType = source.ContentType,
            ArtifactId = source.ArtifactId,
            IntegrityVerified = integrityVerified,
        };
    }

    private static WorkflowRunModelCallBodyPage PageBodyUtf8(WorkflowRunModelCallBodyReadRequest request, BodyContent content, int limit)
    {
        if (request.OffsetBytes > content.TotalBytes)
            return BodyUnavailable(request, WorkflowRunModelCallPartAvailability.InvalidOffset, "The byte offset exceeds this body's length.", content.Source, content.Metadata);
        if (content.Bytes.Length > 0 && IsContinuation(content.Bytes[0]))
            return BodyUnavailable(request, WorkflowRunModelCallPartAvailability.InvalidOffset, "The byte offset is not a UTF-8 character boundary.", content.Source, content.Metadata);

        var consumed = 0;
        var max = Math.Min(limit, content.Bytes.Length);
        while (consumed < max)
        {
            var status = Rune.DecodeFromUtf8(content.Bytes.AsSpan(consumed), out _, out var runeBytes);
            if (status == OperationStatus.Done && consumed + runeBytes <= max)
            {
                consumed += runeBytes;
                continue;
            }
            if (status == OperationStatus.NeedMoreData || status == OperationStatus.Done) break;
            return BodyUnavailable(request, WorkflowRunModelCallPartAvailability.IntegrityFailure, "The stored body is not valid UTF-8 text.", content.Source, content.Metadata);
        }

        if (consumed == 0 && request.OffsetBytes < content.TotalBytes)
            return BodyUnavailable(request, WorkflowRunModelCallPartAvailability.IntegrityFailure, "The stored body could not produce a complete UTF-8 character.", content.Source, content.Metadata);

        var next = request.OffsetBytes + consumed;
        return new WorkflowRunModelCallBodyPage
        {
            Body = request.Body,
            AttemptId = request.AttemptId,
            CaptureCompleteness = content.Source.CaptureCompleteness,
            Availability = WorkflowRunModelCallPartAvailability.Available,
            Text = Encoding.UTF8.GetString(content.Bytes, 0, consumed),
            OffsetBytes = request.OffsetBytes,
            ReturnedBytes = consumed,
            TotalBytes = content.TotalBytes,
            NextOffsetBytes = next < content.TotalBytes ? next : null,
            ContentType = content.ContentType,
            ArtifactId = content.Source.ArtifactId,
            IntegrityVerified = content.IntegrityVerified,
        };
    }

    private static WorkflowRunModelCallPartPage Unavailable(WorkflowRunModelCallPartReadRequest request, WorkflowRunModelCallPartAvailability availability, string message, PartSource source, long? totalBytes = null) => new()
    {
        Part = request.Part,
        Availability = availability,
        OffsetBytes = request.OffsetBytes,
        ReturnedBytes = 0,
        TotalBytes = totalBytes ?? source.SizeBytes,
        ContentType = source.ContentType,
        ArtifactId = source.ArtifactId,
        Message = message,
    };

    private static WorkflowRunModelCallBodyPage BodyUnavailable(WorkflowRunModelCallBodyReadRequest request,
        WorkflowRunModelCallPartAvailability availability, string message, BodyRow source, BodyMetadata? metadata = null) => new()
    {
        Body = request.Body,
        AttemptId = request.AttemptId,
        CaptureCompleteness = source.CaptureCompleteness,
        Availability = availability,
        OffsetBytes = request.OffsetBytes,
        ReturnedBytes = 0,
        TotalBytes = metadata?.TotalBytes,
        ContentType = metadata?.ContentType,
        ArtifactId = source.ArtifactId,
        Message = message,
    };

    private static WorkflowRunModelCallPartAvailability Map(ArtifactRangeReadState state) => state switch
    {
        ArtifactRangeReadState.MetadataMissing => WorkflowRunModelCallPartAvailability.MetadataMissing,
        ArtifactRangeReadState.PhysicalObjectMissing => WorkflowRunModelCallPartAvailability.PhysicalObjectMissing,
        ArtifactRangeReadState.IntegrityFailure => WorkflowRunModelCallPartAvailability.IntegrityFailure,
        ArtifactRangeReadState.BackendUnavailable => WorkflowRunModelCallPartAvailability.BackendUnavailable,
        ArtifactRangeReadState.AccessDenied => WorkflowRunModelCallPartAvailability.AccessDenied,
        ArtifactRangeReadState.InvalidOffset => WorkflowRunModelCallPartAvailability.InvalidOffset,
        _ => WorkflowRunModelCallPartAvailability.BackendUnavailable,
    };

    private static string Message(ArtifactRangeReadState state) => state switch
    {
        ArtifactRangeReadState.MetadataMissing => "The artifact metadata is unavailable.",
        ArtifactRangeReadState.PhysicalObjectMissing => "The stored bytes are unavailable from the configured artifact backend.",
        ArtifactRangeReadState.IntegrityFailure => "The stored bytes failed integrity validation.",
        ArtifactRangeReadState.AccessDenied => "The configured artifact backend denied access.",
        ArtifactRangeReadState.InvalidOffset => "The byte offset is invalid.",
        _ => "The artifact backend is temporarily unavailable.",
    };

    private static bool TryGetField(string payloadJson, string field, out JsonElement value)
    {
        value = default;
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (!document.RootElement.TryGetProperty(field, out var element)) return false;
            value = element.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? PrettyField(string payloadJson, string field) =>
        TryGetField(payloadJson, field, out var element) && element.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? JsonSerializer.Serialize(element, Pretty) : null;

    private static string BuildTrace(WorkflowRunRecord? started, WorkflowRunRecord completed)
    {
        var trace = new StringBuilder();
        if (started is not null) trace.Append("── interaction.started ──\n").Append(PrettyOrRaw(started.PayloadJson)).Append("\n\n");
        trace.Append("── ").Append(completed.RecordType).Append(" ──\n").Append(PrettyOrRaw(completed.PayloadJson));
        return trace.ToString();
    }

    private static string PrettyOrRaw(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return JsonSerializer.Serialize(document.RootElement, Pretty);
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static bool IsContinuation(byte value) => (value & 0b1100_0000) == 0b1000_0000;

    private sealed record CallRecords(WorkflowRunRecord? Started, WorkflowRunRecord Completed);

    private sealed record BodyRow(Guid? ArtifactId, WorkflowRunCaptureCompleteness CaptureCompleteness);

    private sealed record BodyMetadata(long? TotalBytes, string? ContentType);

    private sealed record BodyContent(BodyRow Source, byte[] Bytes, long TotalBytes, string ContentType, bool IntegrityVerified)
    {
        public BodyMetadata Metadata { get; } = new(TotalBytes, ContentType);
    }

    private sealed record AttemptBodyRow(Guid? RequestArtifactId, Guid? ResponseArtifactId, Guid? ErrorArtifactId,
        WorkflowRunCaptureCompleteness CaptureCompleteness)
    {
        public Guid? Artifact(WorkflowRunModelCallBody body) => body switch
        {
            WorkflowRunModelCallBody.AttemptRequest => RequestArtifactId,
            WorkflowRunModelCallBody.AttemptResponse => ResponseArtifactId,
            WorkflowRunModelCallBody.AttemptError => ErrorArtifactId,
            _ => null,
        };
    }

    private sealed record PartSource(WorkflowRunModelCallPartSource Kind, string? InlineText, Guid? ArtifactId, long? SizeBytes, string? ContentType)
    {
        public static PartSource NotRecorded { get; } = new(WorkflowRunModelCallPartSource.NotRecorded, null, null, null, null);

        public WorkflowRunModelCallPartDescriptor Descriptor(WorkflowRunModelCallPart part) => new()
        {
            Part = part,
            Source = Kind,
            SizeBytes = SizeBytes,
            ContentType = ContentType,
            ArtifactId = ArtifactId,
        };
    }
}

using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// One UTF-8-safe byte window of one exact top-level cell field. <see cref="Text"/> is independently parseable JSON
/// only when <see cref="CompleteJsonValue"/> is true; this API does not claim nested structured pagination.
/// Artifact identity and storage location never cross this seam.
/// </summary>
public sealed record WorkflowRunCellFieldRangePage
{
    public required Guid RequestedRunId { get; init; }
    public required WorkflowRunViewScope Scope { get; init; }
    public required Guid SourceRunId { get; init; }
    public required string NodeId { get; init; }
    public required string IterationKey { get; init; }
    public required Guid StateRecordId { get; init; }
    public required long StateRecordSequence { get; init; }
    public Guid? FirstStartedRecordId { get; init; }
    public long? FirstStartedRecordSequence { get; init; }
    public required NodeStatus Status { get; init; }
    public required WorkflowRunCellFieldSection Section { get; init; }
    /// <summary>Null only for Error; an empty Input/Output property name is valid.</summary>
    public string? Name { get; init; }
    public required WorkflowRunCellFieldRangeAvailability Availability { get; init; }
    public required WorkflowRunCellFieldRangeSource Source { get; init; }
    public string? RequestCursor { get; init; }
    public required int LimitBytes { get; init; }
    public required long OffsetBytes { get; init; }
    public required int ReturnedBytes { get; init; }
    public long? TotalBytes { get; init; }
    public string? NextCursor { get; init; }
    public string? Text { get; init; }
    public string? ContentType { get; init; }
    public required bool IntegrityVerified { get; init; }
    public required bool CompleteJsonValue { get; init; }
    public required bool Retryable { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunCellFieldRangeAvailability
{
    Available,
    NotRecorded,
    StaleIdentity,
    CorruptReference,
    MetadataMissing,
    PhysicalObjectMissing,
    IntegrityFailure,
    BackendUnavailable,
    AccessDenied,
    InvalidRange,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunCellFieldRangeSource
{
    Unavailable,
    Inline,
    Artifact,
}

/// <summary>Every public coordinate bound by a range cursor. Artifact identity is deliberately absent.</summary>
public sealed record WorkflowRunCellFieldRangeIdentity
{
    public required Guid RequestedRunId { get; init; }
    public required WorkflowRunViewScope Scope { get; init; }
    public required Guid SourceRunId { get; init; }
    public required string NodeId { get; init; }
    public required string IterationKey { get; init; }
    public required WorkflowRunCellRecordIdentity Records { get; init; }
    public required WorkflowRunCellFieldSection Section { get; init; }
    public string? Name { get; init; }
}

/// <summary>Opaque v1 continuation cursor bound to an immutable cell observation and one selected field.</summary>
public readonly record struct WorkflowRunCellFieldRangeCursor(WorkflowRunCellFieldRangeIdentity Identity, long OffsetBytes)
{
    private const string Version = "v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public const int MaximumEncodedLength = 16 * 1024;

    public string Encode()
    {
        if (OffsetBytes < 0) throw new InvalidOperationException("Workflow Run cell-field range cursor offset must be non-negative.");
        var records = Identity.Records;
        var raw = string.Create(CultureInfo.InvariantCulture,
            $"{Version}\n{Identity.RequestedRunId:N}\n{(int)Identity.Scope}\n{Identity.SourceRunId:N}\n{Token(Identity.NodeId)}\n{Token(Identity.IterationKey)}\n{records.StateRecordId:N}\n{records.StateRecordSequence}\n{records.FirstStartedRecordId?.ToString("N") ?? "-"}\n{records.FirstStartedRecordSequence?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n{(int)Identity.Section}\n{(Identity.Name is null ? "-" : Token(Identity.Name))}\n{OffsetBytes}");
        var encoded = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
        if (encoded.Length > MaximumEncodedLength) throw new InvalidOperationException("Workflow Run cell-field range cursor exceeded its wire bound.");
        return encoded;
    }

    public static bool TryDecode(string? value, out WorkflowRunCellFieldRangeCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEncodedLength) return false;

        try
        {
            var parts = StrictUtf8.GetString(Base64Url.DecodeFromChars(value)).Split('\n');
            if (parts.Length != 13 || parts[0] != Version
                || !Guid.TryParseExact(parts[1], "N", out var requestedRunId) || requestedRunId == Guid.Empty
                || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var scopeValue)
                || !Enum.IsDefined(typeof(WorkflowRunViewScope), scopeValue)
                || !Guid.TryParseExact(parts[3], "N", out var sourceRunId) || sourceRunId == Guid.Empty
                || !TryToken(parts[4], out var nodeId) || !TryToken(parts[5], out var iterationKey)
                || !Guid.TryParseExact(parts[6], "N", out var stateId) || stateId == Guid.Empty
                || !long.TryParse(parts[7], NumberStyles.None, CultureInfo.InvariantCulture, out var stateSequence) || stateSequence <= 0
                || !int.TryParse(parts[10], NumberStyles.None, CultureInfo.InvariantCulture, out var sectionValue)
                || !Enum.IsDefined(typeof(WorkflowRunCellFieldSection), sectionValue)
                || !long.TryParse(parts[12], NumberStyles.None, CultureInfo.InvariantCulture, out var offset)) return false;

            Guid? firstId = null;
            long? firstSequence = null;
            if (parts[8] != "-" || parts[9] != "-")
            {
                if (!Guid.TryParseExact(parts[8], "N", out var parsedFirstId) || parsedFirstId == Guid.Empty
                    || !long.TryParse(parts[9], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedFirstSequence)
                    || parsedFirstSequence <= 0) return false;
                firstId = parsedFirstId;
                firstSequence = parsedFirstSequence;
            }

            string? name = null;
            if (parts[11] != "-" && !TryToken(parts[11], out name)) return false;
            cursor = new WorkflowRunCellFieldRangeCursor(new WorkflowRunCellFieldRangeIdentity
            {
                RequestedRunId = requestedRunId,
                Scope = (WorkflowRunViewScope)scopeValue,
                SourceRunId = sourceRunId,
                NodeId = nodeId!,
                IterationKey = iterationKey!,
                Records = new WorkflowRunCellRecordIdentity(stateId, stateSequence, firstId, firstSequence),
                Section = (WorkflowRunCellFieldSection)sectionValue,
                Name = name,
            }, offset);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }

    private static string Token(string value) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(value));

    private static bool TryToken(string value, out string? decoded)
    {
        decoded = null;
        try
        {
            decoded = StrictUtf8.GetString(Base64Url.DecodeFromChars(value));
            return true;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }
}

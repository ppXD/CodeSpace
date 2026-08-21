using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;

namespace CodeSpace.Messages.Dtos.Workflows;

/// <summary>
/// One bounded metadata page for the fields owned by one exact Workflow Run cell. No field body, storage locator,
/// artifact id, node configuration or record payload crosses this seam.
/// </summary>
public sealed record WorkflowRunCellFieldPage
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
    public string? RequestCursor { get; init; }
    public required int Limit { get; init; }
    public required WorkflowRunCellFieldAvailability FieldsAvailability { get; init; }
    public required WorkflowRunCellFieldAvailability InputsAvailability { get; init; }
    public required WorkflowRunCellFieldAvailability OutputsAvailability { get; init; }
    public required WorkflowRunCellFieldAvailability ErrorAvailability { get; init; }
    public required IReadOnlyList<WorkflowRunCellFieldDescriptor> Fields { get; init; }
    public string? NextCursor { get; init; }
}

/// <summary>
/// Metadata for one top-level input/output property or the cell error scalar. Inline sizes are intentionally deferred
/// until the selected-field byte reader: computing them here would stringify every large value merely to list names.
/// Artifact metadata is the exact team-owned row's size/digest, never the untrusted values declared by the pointer.
/// </summary>
public sealed record WorkflowRunCellFieldDescriptor
{
    public required WorkflowRunCellFieldSection Section { get; init; }
    /// <summary>The exact JSON property name. Null only for the Error scalar; an empty input/output name is valid.</summary>
    public string? Name { get; init; }
    public required WorkflowRunCellFieldJsonKind JsonKind { get; init; }
    public required WorkflowRunCellFieldMaterialization Materialization { get; init; }
    public required WorkflowRunCellFieldAvailability Availability { get; init; }
    public long? TotalBytes { get; init; }
    public string? Sha256 { get; init; }
    public required string ContentType { get; init; }
    public WorkflowRunCellFieldProblemCode? ProblemCode { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunCellFieldSection
{
    Input = 0,
    Output = 1,
    Error = 2,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunCellFieldAvailability
{
    Available,
    NotRecorded,
    CorruptReference,
    NameTooLarge,
    Truncated,
    Unavailable,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunCellFieldMaterialization
{
    Inline,
    Artifact,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunCellFieldJsonKind
{
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
    Unknown,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunCellFieldProblemCode
{
    MalformedReference,
    ArtifactMetadataMissing,
    DeclaredSizeMismatch,
    DeclaredContentTypeMismatch,
    StoredContentTypeMismatch,
}

/// <summary>
/// Opaque v1 keyset bound to the immutable records that supplied the page. A later node state or newly admitted first
/// start makes the cursor stale instead of mixing descriptors from two observations.
/// </summary>
public readonly record struct WorkflowRunCellRecordIdentity(Guid StateRecordId, long StateRecordSequence,
    Guid? FirstStartedRecordId, long? FirstStartedRecordSequence);

public readonly record struct WorkflowRunCellFieldCursor(WorkflowRunCellRecordIdentity Records,
    WorkflowRunCellFieldSection Section, string Name)
{
    private const string Version = "v1";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    public const int MaximumEncodedLength = 8192;

    public string Encode()
    {
        var encodedName = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(Name));
        var raw = string.Create(CultureInfo.InvariantCulture,
            $"{Version}\n{Records.StateRecordId:N}\n{Records.StateRecordSequence}\n{Records.FirstStartedRecordId?.ToString("N") ?? "-"}\n{Records.FirstStartedRecordSequence?.ToString(CultureInfo.InvariantCulture) ?? "-"}\n{(int)Section}\n{encodedName}");
        var encoded = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
        if (encoded.Length > MaximumEncodedLength) throw new InvalidOperationException("Workflow Run cell-field cursor exceeded its wire bound.");
        return encoded;
    }

    public static bool TryDecode(string? value, out WorkflowRunCellFieldCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEncodedLength) return false;

        try
        {
            var parts = StrictUtf8.GetString(Base64Url.DecodeFromChars(value)).Split('\n');
            if (parts.Length != 7 || parts[0] != Version
                || !Guid.TryParseExact(parts[1], "N", out var stateId) || stateId == Guid.Empty
                || !long.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var stateSequence) || stateSequence <= 0
                || !int.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var sectionValue)
                || !Enum.IsDefined(typeof(WorkflowRunCellFieldSection), sectionValue)) return false;

            Guid? firstId = null;
            long? firstSequence = null;
            if (parts[3] != "-" || parts[4] != "-")
            {
                if (!Guid.TryParseExact(parts[3], "N", out var parsedFirstId) || parsedFirstId == Guid.Empty
                    || !long.TryParse(parts[4], NumberStyles.None, CultureInfo.InvariantCulture, out var parsedFirstSequence)
                    || parsedFirstSequence <= 0) return false;
                firstId = parsedFirstId;
                firstSequence = parsedFirstSequence;
            }

            var name = StrictUtf8.GetString(Base64Url.DecodeFromChars(parts[6]));
            cursor = new WorkflowRunCellFieldCursor(new WorkflowRunCellRecordIdentity(stateId, stateSequence, firstId, firstSequence),
                (WorkflowRunCellFieldSection)sectionValue, name);
            return true;
        }
        catch (Exception exception) when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }
    }
}

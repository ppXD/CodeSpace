using System.Buffers.Text;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Dtos.Workflows.ToolCalls;

/// <summary>
/// One metadata-only observation from the Workflow Run tool-call plane. The current producer covers terminal,
/// governed side-effecting ToolCallLedger calls only; this is not an all-tools feed. Native CLI activity remains in
/// Agent Run events. <see cref="CallOrdinal"/> is the source Agent Run's admission ordinal and is neither unique nor
/// ordered across a Workflow Run, so pages use CreatedAt plus ToolCallId instead.
/// </summary>
public sealed record WorkflowRunToolCallMetadata
{
    public required Guid ToolCallId { get; init; }
    public required Guid RunId { get; init; }
    public required string ToolAdapterKind { get; init; }
    public required string ToolName { get; init; }
    public required WorkflowRunToolCallEffectClass EffectClass { get; init; }
    public required WorkflowRunToolCallObservationState State { get; init; }
    public required long CallOrdinal { get; init; }
    public string? SourceKind { get; init; }
    public Guid? SourceCorrelationId { get; init; }
    public required string CaptureSource { get; init; }
    public required WorkflowRunCaptureCompleteness CaptureCompleteness { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastModifiedAt { get; init; }
    public DateTimeOffset? TerminalAt { get; init; }
    /// <summary>A closed reason code only. Raw error strings, messages and error payloads are never exposed here.</summary>
    public WorkflowRunToolCallObservationErrorCode? ErrorCode { get; init; }
}

public sealed record WorkflowRunToolCallAttemptMetadata
{
    public required int AttemptOrdinal { get; init; }
    public required WorkflowRunToolCallAttemptObservationStatus Status { get; init; }
    public required string CaptureSource { get; init; }
    public required WorkflowRunCaptureCompleteness CaptureCompleteness { get; init; }
    /// <summary>
    /// The present ToolCallLedger adapter records source admission as this lower bound. It is not an observed provider
    /// wire-dispatch time and must not be used as exact provider latency.
    /// </summary>
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
    public required DateTimeOffset LastModifiedAt { get; init; }
    /// <summary>A closed reason code only. Raw error strings, messages and error payloads are never exposed here.</summary>
    public WorkflowRunToolCallObservationErrorCode? ErrorCode { get; init; }
}

public sealed record WorkflowRunToolCallPage
{
    public required Guid RunId { get; init; }
    /// <summary>The exact validated cursor that produced this page; null for the first page.</summary>
    public string? RequestCursor { get; init; }
    /// <summary>The validated hard page limit applied by the reader.</summary>
    public required int Limit { get; init; }
    public required IReadOnlyList<WorkflowRunToolCallMetadata> Items { get; init; }
    public string? NextCursor { get; init; }
}

public sealed record WorkflowRunToolCallDetail
{
    public required WorkflowRunToolCallMetadata Call { get; init; }
    public required IReadOnlyList<WorkflowRunToolCallAttemptMetadata> Attempts { get; init; }
    public required bool AttemptsTruncated { get; init; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunToolCallEffectClass
{
    ReadOnly,
    SideEffecting,
    Unknown,
    LegacyUnknown,
    Corrupt,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunToolCallObservationState
{
    Pending,
    Running,
    Completed,
    Abandoned,
    LegacyUnknown,
    Corrupt,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunToolCallAttemptObservationStatus
{
    Pending,
    Running,
    Succeeded,
    Failed,
    Denied,
    Cancelled,
    TimedOut,
    Indeterminate,
    LegacyUnknown,
    Corrupt,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WorkflowRunToolCallObservationErrorCode
{
    LedgerFailedOutcomeUnknown,
    GovernanceDenied,
    ApprovalExpired,
    LegacyUnknown,
    Corrupt,
}

/// <summary>Opaque, versioned CreatedAt + stable-id keyset cursor. CallOrdinal is deliberately absent.</summary>
public readonly record struct WorkflowRunToolCallPageCursor(DateTimeOffset CreatedAt, Guid Id)
{
    private const string Version = "v1";
    public const int MaximumEncodedLength = 96;

    public string Encode()
    {
        var raw = string.Create(CultureInfo.InvariantCulture, $"{Version}\n{CreatedAt.UtcTicks}\n{Id:N}");
        return Base64Url.EncodeToString(Encoding.UTF8.GetBytes(raw));
    }

    public static WorkflowRunToolCallPageCursor? Decode(string? value)
    {
        if (value is null) return null;
        if (TryDecode(value, out var cursor)) return cursor;
        throw new FormatException("Invalid Workflow Run tool-call page cursor.");
    }

    public static bool TryDecode(string? value, out WorkflowRunToolCallPageCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumEncodedLength) return false;

        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)).Split('\n');
            if (parts.Length != 3 || parts[0] != Version
                || !long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks
                || !Guid.TryParseExact(parts[2], "N", out var id) || id == Guid.Empty) return false;

            cursor = new WorkflowRunToolCallPageCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

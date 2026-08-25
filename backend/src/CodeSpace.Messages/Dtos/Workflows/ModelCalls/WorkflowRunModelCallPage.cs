using System.Buffers.Text;
using System.Globalization;
using System.Text;
using CodeSpace.Messages.Contracts;

namespace CodeSpace.Messages.Dtos.Workflows.ModelCalls;

/// <summary>One body-free index row shared by every Workflow Run model-call producer.</summary>
public sealed record WorkflowRunModelCallListItem
{
    public required Guid WorkflowRunModelCallId { get; init; }
    public required Guid RunId { get; init; }
    public required long CallOrdinal { get; init; }
    public string? NodeId { get; init; }
    public required string IterationKey { get; init; }
    public Guid? ExecutionAttemptId { get; init; }
    public required string Purpose { get; init; }
    public string? RequestedProvider { get; init; }
    public string? RequestedModel { get; init; }
    public required string CaptureSource { get; init; }
    public required WorkflowRunCaptureCompleteness CaptureCompleteness { get; init; }
    public required DateTimeOffset CreatedAt { get; init; }
}

public sealed record WorkflowRunModelCallPage
{
    public required Guid RunId { get; init; }
    public string? RequestCursor { get; init; }
    public required int Limit { get; init; }
    public required IReadOnlyList<WorkflowRunModelCallListItem> Items { get; init; }
    public string? NextCursor { get; init; }
}

public readonly record struct WorkflowRunModelCallPageCursor(DateTimeOffset CreatedAt, Guid Id)
{
    public string Encode() => Base64Url.EncodeToString(Encoding.UTF8.GetBytes($"{CreatedAt.UtcTicks.ToString(CultureInfo.InvariantCulture)}|{Id:D}"));

    public static bool TryDecode(string? value, out WorkflowRunModelCallPageCursor cursor)
    {
        cursor = default;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128) return false;
        try
        {
            var parts = Encoding.UTF8.GetString(Base64Url.DecodeFromChars(value)).Split('|');
            if (parts.Length != 2 || !long.TryParse(parts[0], CultureInfo.InvariantCulture, out var ticks)
                || ticks < DateTimeOffset.MinValue.Ticks || ticks > DateTimeOffset.MaxValue.Ticks
                || !Guid.TryParseExact(parts[1], "D", out var id) || id == Guid.Empty) return false;
            cursor = new WorkflowRunModelCallPageCursor(new DateTimeOffset(ticks, TimeSpan.Zero), id);
            return true;
        }
        catch (FormatException) { return false; }
    }
}

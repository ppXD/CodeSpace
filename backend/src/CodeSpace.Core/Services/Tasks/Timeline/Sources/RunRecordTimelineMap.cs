using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Pure mapping from ONE <c>workflow_run_record</c> ledger row to a narrative timeline event (or <c>null</c> to drop
/// it). Only the NARRATIVE-worthy lifecycle records map; Trace-level noise (release / scope / variables snapshots,
/// log lines, iteration + external-call detail) returns null and is left to the Trace tab. Extracted from the
/// source so the per-record-type decision is unit-testable without a database.
/// </summary>
public static class RunRecordTimelineMap
{
    /// <summary>The lifecycle source's provenance key — stamped on every event this mapper emits.</summary>
    public const string Key = "run-record";

    public static RunTimelineEvent? ToEvent(WorkflowRunRecord r)
    {
        var node = r.NodeId ?? "node";

        return r.RecordType switch
        {
            WorkflowRunRecordTypes.RunStarted    => Event(r, "Run started", TimelineSeverity.Info),
            WorkflowRunRecordTypes.RunCompleted  => Event(r, "Run completed", TimelineSeverity.Success),
            WorkflowRunRecordTypes.RunFailed     => Event(r, "Run failed", TimelineSeverity.Error, ReadString(r, "error")),
            WorkflowRunRecordTypes.RunCancelled  => Event(r, "Run cancelled", TimelineSeverity.Warning),
            WorkflowRunRecordTypes.RunReplayed   => Event(r, "Run replayed", TimelineSeverity.Info),
            WorkflowRunRecordTypes.NodeStarted   => Event(r, $"{node} started", TimelineSeverity.Info),
            WorkflowRunRecordTypes.NodeCompleted => Event(r, $"{node} completed", TimelineSeverity.Success),
            WorkflowRunRecordTypes.NodeFailed    => Event(r, $"{node} failed", TimelineSeverity.Error, ReadString(r, "error")),
            WorkflowRunRecordTypes.NodeSuspended => Event(r, $"{node} waiting", TimelineSeverity.Warning, ReadString(r, "wait_kind")),
            WorkflowRunRecordTypes.NodeSkipped   => Event(r, $"{node} skipped", TimelineSeverity.Info, ReadString(r, "reason")),
            WorkflowRunRecordTypes.AttemptFailed => Event(r, $"{node} retry", TimelineSeverity.Warning, RetrySummary(r)),
            _ => null,
        };
    }

    private static RunTimelineEvent Event(WorkflowRunRecord r, string title, TimelineSeverity severity, string? summary = null) =>
        new()
        {
            Id = $"record-{r.Sequence}",
            Kind = r.RecordType,
            Title = title,
            Summary = summary,
            Severity = severity,
            OccurredAt = r.OccurredAt,
            NodeId = r.NodeId,
            SourceKey = Key,
        };

    private static string? RetrySummary(WorkflowRunRecord r)
    {
        var attempt = ReadInt(r, "attempt");
        var max = ReadInt(r, "max_attempts");
        var prefix = attempt != null && max != null ? $"attempt {attempt}/{max}" : null;

        return Join(prefix, ReadString(r, "error"));
    }

    private static string? Join(string? a, string? b) => a != null && b != null ? $"{a}: {b}" : a ?? b;

    private static string? ReadString(WorkflowRunRecord r, string prop) =>
        Read(r, prop, e => e.ValueKind == JsonValueKind.String ? e.GetString() : null);

    private static int? ReadInt(WorkflowRunRecord r, string prop) =>
        Read(r, prop, e => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var n) ? n : (int?)null);

    private static T? Read<T>(WorkflowRunRecord r, string prop, Func<JsonElement, T?> read)
    {
        try
        {
            using var doc = JsonDocument.Parse(r.PayloadJson);
            return doc.RootElement.TryGetProperty(prop, out var v) ? read(v) : default;
        }
        catch (JsonException)
        {
            return default;
        }
    }
}

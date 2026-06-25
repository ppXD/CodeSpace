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

        // Run-level lifecycle, a TOP-LEVEL node FAILURE, and a retry are story MILESTONES; the per-node started/
        // completed/waiting/skipped churn — and a fanned-out BRANCH / loop-iteration / try-body failure — is DETAIL the
        // UI folds away (the wave already shows the agent's progress, the branch's own terminal carries its error, Trace
        // has the rest). See NodeFailureLevel.
        return r.RecordType switch
        {
            WorkflowRunRecordTypes.RunStarted    => Event(r, "Run started", TimelineSeverity.Info, TimelineLevel.Milestone),
            WorkflowRunRecordTypes.RunCompleted  => Event(r, "Run completed", TimelineSeverity.Success, TimelineLevel.Milestone),
            WorkflowRunRecordTypes.RunFailed     => Event(r, "Run failed", TimelineSeverity.Error, TimelineLevel.Milestone, ReadString(r, "error")),
            WorkflowRunRecordTypes.RunCancelled  => Event(r, "Run cancelled", TimelineSeverity.Warning, TimelineLevel.Milestone),
            WorkflowRunRecordTypes.RunReplayed   => Event(r, "Run replayed", TimelineSeverity.Info, TimelineLevel.Milestone),
            WorkflowRunRecordTypes.NodeStarted   => Event(r, $"{node} started", TimelineSeverity.Info, TimelineLevel.Detail),
            WorkflowRunRecordTypes.NodeCompleted => Event(r, $"{node} completed", TimelineSeverity.Success, TimelineLevel.Detail),
            WorkflowRunRecordTypes.NodeFailed    => Event(r, $"{node} failed", TimelineSeverity.Error, NodeFailureLevel(r), ReadString(r, "error")),
            WorkflowRunRecordTypes.NodeSuspended => Event(r, $"{node} waiting", TimelineSeverity.Warning, TimelineLevel.Detail, ReadString(r, "wait_kind")),
            WorkflowRunRecordTypes.NodeSkipped   => Event(r, $"{node} skipped", TimelineSeverity.Info, TimelineLevel.Detail, ReadString(r, "reason")),
            WorkflowRunRecordTypes.AttemptFailed => Event(r, $"{node} retry", TimelineSeverity.Warning, TimelineLevel.Milestone, RetrySummary(r)),
            _ => null,
        };
    }

    /// <summary>
    /// A node FAILURE is a story MILESTONE — EXCEPT a fanned-out branch / loop-iteration / try-body failure, which is
    /// per-iteration DETAIL the UI folds. A non-empty <see cref="WorkflowRunRecord.IterationKey"/> marks such a nested
    /// row (<c>map#i</c> / <c>loop#i</c> / nested <c>a#i/b#j</c>); a 12-branch map that fails every branch would
    /// otherwise flood the narrative with 12 identical "agent failed" milestones below the fan-out card. The
    /// CONTAINER's own failure (the map / loop / try node, empty key) and every top-level node failure stay milestones,
    /// so the story still names WHAT failed; each branch's own error lives on its agent terminal + the Trace tab.
    /// </summary>
    private static TimelineLevel NodeFailureLevel(WorkflowRunRecord r) =>
        string.IsNullOrEmpty(r.IterationKey) ? TimelineLevel.Milestone : TimelineLevel.Detail;

    private static RunTimelineEvent Event(WorkflowRunRecord r, string title, TimelineSeverity severity, TimelineLevel level, string? summary = null) =>
        new()
        {
            Id = $"record-{r.Sequence}",
            Kind = r.RecordType,
            Title = title,
            Summary = summary,
            Severity = severity,
            Level = level,
            OccurredAt = r.OccurredAt,
            Order = r.Sequence,   // the ledger's monotonic order — the same-OccurredAt tie-break
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

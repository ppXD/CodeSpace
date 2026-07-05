using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Llm;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Pure mapping from ONE <c>workflow_run_record</c> ledger row to a narrative timeline event (or <c>null</c> to drop
/// it). The NARRATIVE-worthy lifecycle records map, plus a model call's OUTCOME (<c>interaction.completed</c>/<c>failed</c>)
/// folded to a Detail event carrying its kind + model + token cost — the substance of an AI workflow. Trace-level noise
/// (release / scope / variables snapshots, log lines, iteration + external-call http detail, the interaction.started open
/// bracket) returns null and is left to the Trace tab. Extracted from the source so the per-record-type decision is
/// unit-testable without a database.
/// </summary>
public static class RunRecordTimelineMap
{
    /// <summary>The lifecycle source's provenance key — stamped on every event this mapper emits.</summary>
    public const string Key = "run-record";

    /// <summary>
    /// Map an ORDERED record stream to events, folding the durable-RESUME mechanics. The engine writes a
    /// <c>RunStarted</c> on EVERY dispatch — the first run AND every resume — and a <c>RunReplayed</c> on every resume
    /// (it rebuilds scope from the snapshot). A node that suspends/resumes per step (the supervisor decides once per
    /// turn; a long sleep / sub-workflow / HITL wait parks + wakes) therefore emits these N times. Only the FIRST
    /// <c>RunStarted</c> is a story milestone; every later <c>RunStarted</c> + ALL <c>RunReplayed</c> are resume DETAIL
    /// the UI folds into a "N steps" disclosure. Generic — keyed on the record type + first-seen, never on a node id.
    /// </summary>
    public static IReadOnlyList<RunTimelineEvent> Project(IEnumerable<WorkflowRunRecord> orderedRecords)
    {
        var events = new List<RunTimelineEvent>();
        var firstRunStartSeen = false;

        foreach (var r in orderedRecords)
        {
            var e = ToEvent(r);
            if (e == null) continue;

            if (r.RecordType == WorkflowRunRecordTypes.RunStarted)
            {
                if (firstRunStartSeen) e = e with { Level = TimelineLevel.Detail };
                firstRunStartSeen = true;
            }

            events.Add(e);
        }

        return events;
    }

    public static RunTimelineEvent? ToEvent(WorkflowRunRecord r)
    {
        var node = r.NodeId ?? "node";

        // Run-level lifecycle, a TOP-LEVEL node FAILURE, and a retry are story MILESTONES; the per-node started/
        // completed/waiting/skipped churn — and a fanned-out BRANCH / loop-iteration / try-body failure — is DETAIL the
        // UI folds away (the wave already shows the agent's progress, the branch's own terminal carries its error, Trace
        // has the rest). See NodeFailureLevel. A RESUME's RunStarted is demoted to Detail by Project (the first stays a
        // milestone); RunReplayed is ALWAYS a resume mechanic, never a milestone.
        return r.RecordType switch
        {
            WorkflowRunRecordTypes.RunStarted    => Event(r, "Run started", TimelineSeverity.Info, TimelineLevel.Milestone),
            WorkflowRunRecordTypes.RunCompleted  => Event(r, "Run completed", TimelineSeverity.Success, TimelineLevel.Milestone),
            WorkflowRunRecordTypes.RunFailed     => Event(r, "Run failed", TimelineSeverity.Error, TimelineLevel.Milestone, ReadString(r, "error")),
            WorkflowRunRecordTypes.RunCancelled  => Event(r, "Run cancelled", TimelineSeverity.Warning, TimelineLevel.Milestone),
            WorkflowRunRecordTypes.RunReplayed   => Event(r, "Run replayed", TimelineSeverity.Info, TimelineLevel.Detail),
            WorkflowRunRecordTypes.NodeStarted   => Event(r, $"{node} started", TimelineSeverity.Info, TimelineLevel.Detail),
            WorkflowRunRecordTypes.NodeCompleted => Event(r, $"{node} completed", TimelineSeverity.Success, TimelineLevel.Detail),
            WorkflowRunRecordTypes.NodeFailed    => Event(r, $"{node} failed", TimelineSeverity.Error, NodeFailureLevel(r), ReadString(r, "error")),
            WorkflowRunRecordTypes.NodeSuspended => Event(r, $"{node} waiting", TimelineSeverity.Warning, TimelineLevel.Detail, ReadString(r, "wait_kind")),
            WorkflowRunRecordTypes.NodeSkipped   => Event(r, $"{node} skipped", TimelineSeverity.Info, TimelineLevel.Detail, ReadString(r, "reason")),
            WorkflowRunRecordTypes.AttemptFailed => Event(r, $"{node} retry", TimelineSeverity.Warning, TimelineLevel.Milestone, RetrySummary(r)),

            // An operator force-resolved a stranded wait — a manual intervention that explains WHY a parked run resumed,
            // so it's a story MILESTONE (Warning-toned: it's an override of the normal signal path).
            WorkflowRunRecordTypes.WaitReissued  => Event(r, "Wait re-issued", TimelineSeverity.Warning, TimelineLevel.Milestone, ReadString(r, "wait_kind")),

            // A model call is the SUBSTANCE of an AI workflow (which model decided what, at what token cost), so — unlike
            // the external_call.* http plumbing left to Trace — its OUTCOME is narrative, folded to Detail: the completed
            // record carries the kind + model + token usage; the failed one the error. The interaction.started open
            // bracket stays Trace-only (it adds no outcome). Generic over EVERY in-process call kind (llm.complete, a
            // plan-author's planner/critic, the supervisor's decision, the agent run's output-review critic).
            WorkflowRunRecordTypes.InteractionCompleted => ModelCallEvent(r),
            WorkflowRunRecordTypes.InteractionFailed    => Event(r, "Model call failed", TimelineSeverity.Error, TimelineLevel.Detail, Join(ReadString(r, "kind"), ReadString(r, "error"))),

            _ => null,
        };
    }

    /// <summary>A completed model call is Info "Model call" UNLESS the provider's finish reason says the answer was CUT OFF — a length-cap truncation or a content filter escalates to Warning with a qualified title ("Model call — output truncated"), so a decision that ran on a cut-off answer never reads as a clean green completion. Reads the SAME finish classifier the fact row does.</summary>
    private static RunTimelineEvent ModelCallEvent(WorkflowRunRecord r)
    {
        var finish = ModelCallFinish.Classify(ReadUsageFinishReason(r));
        var qualifier = ModelCallFinish.Qualifier(finish);

        var title = qualifier == null ? "Model call" : $"Model call — {qualifier}";
        var severity = finish == ModelCallFinishKind.Clean ? TimelineSeverity.Info : TimelineSeverity.Warning;

        return Event(r, title, severity, TimelineLevel.Detail, ModelCallSummary(r));
    }

    /// <summary>The provider stop reason off the completion's nested <c>usage.finishReason</c> — null when absent/malformed (a usage-silent call reads as a clean stop, never a false truncation).</summary>
    private static string? ReadUsageFinishReason(WorkflowRunRecord r)
    {
        try
        {
            using var doc = JsonDocument.Parse(r.PayloadJson);

            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

            return usage.TryGetProperty("finishReason", out var f) && f.ValueKind == JsonValueKind.String ? f.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Fold a completed model call's kind + model + total token usage into the summary — the per-call attribution + cost the timeline previously dropped. Absent fields are omitted (a null model / unpriced usage leaves no dangling separator). e.g. "llm.complete · claude-opus-4-8 · 36 tokens".</summary>
    private static string? ModelCallSummary(WorkflowRunRecord r)
    {
        var tokens = ReadUsageTotalTokens(r);

        return JoinParts(ReadString(r, "kind"), ReadString(r, "model"), tokens != null ? $"{tokens} tokens" : null);
    }

    /// <summary>Total tokens (input + output) off the completion's nested <c>usage</c> object, or null when absent — a model that reported no usage adds no token clause rather than a bogus "0 tokens".</summary>
    private static int? ReadUsageTotalTokens(WorkflowRunRecord r)
    {
        try
        {
            using var doc = JsonDocument.Parse(r.PayloadJson);

            if (!doc.RootElement.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object) return null;

            var input = usage.TryGetProperty("inputTokens", out var i) && i.TryGetInt32(out var iv) ? iv : 0;
            var output = usage.TryGetProperty("outputTokens", out var o) && o.TryGetInt32(out var ov) ? ov : 0;
            var total = input + output;

            return total > 0 ? total : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Join the non-empty parts with " · ", dropping absent fields so a missing model/usage never leaves a dangling separator. Null when nothing is present.</summary>
    private static string? JoinParts(params string?[] parts)
    {
        var present = new List<string>();

        foreach (var p in parts)
            if (!string.IsNullOrEmpty(p)) present.Add(p!);

        return present.Count == 0 ? null : string.Join(" · ", present);
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

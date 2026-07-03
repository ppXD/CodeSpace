using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Pure mapping from ONE side-effecting tool-call ledger row (<c>tool_call_ledger</c>) to a narrative timeline event —
/// the "what the agent DID to the world" story line (opened a PR, committed, ran a governed command). Only
/// side-effecting tools get a ledger row (read-only tools never do), so every mapped row is genuinely narrative-worthy;
/// a FAILED / DENIED call escalates to a <see cref="TimelineLevel.Milestone"/> (a side effect that didn't land is a
/// beat the operator must see), a routine success folds to <see cref="TimelineLevel.Detail"/> under its wave. Severity
/// rides the CLOSED <see cref="ToolCallLedgerStatus"/> axis, never the (open) tool kind, so the source stays
/// tool-neutral. Extracted from the source so the title / severity / level are unit-testable without a database.
/// </summary>
public static class ToolCallTimelineMap
{
    /// <summary>The tool-call source's provenance key — stamped on every event this mapper emits.</summary>
    public const string Key = "tool-calls";

    public static RunTimelineEvent ToEvent(ToolCallLedger call, IReadOnlyDictionary<Guid, string?> nodeByAgent) =>
        new()
        {
            Id = $"tool-{call.Id:N}",
            Kind = $"tool.{call.ToolKind}",   // provenance (never switched on) — matches supervisor.{verb} / agent.{kind}
            Title = $"Called {call.ToolKind}",
            Summary = call.Error,             // the failure / denial reason; null on success / while pending
            Severity = SeverityFor(call.Status),
            Level = LevelFor(call.Status),
            OccurredAt = call.CreatedDate,    // the ledger's chronological key (there is no Sequence column)
            Order = 0,                        // no per-row monotonic cursor — the same-tick tie-break falls to Id
            NodeId = nodeByAgent.TryGetValue(call.AgentRunId, out var node) ? node : null,
            AgentRunId = call.AgentRunId.ToString(),
            SourceKey = Key,
        };

    // A side effect that DIDN'T LAND — failed, denied, or an approval that expired unrun — is a story milestone (the
    // operator must see it); a successful / pending / in-flight call is Detail the wave + the agent's own terminal carry.
    private static TimelineLevel LevelFor(ToolCallLedgerStatus status) => status switch
    {
        ToolCallLedgerStatus.Failed => TimelineLevel.Milestone,
        ToolCallLedgerStatus.Denied => TimelineLevel.Milestone,
        ToolCallLedgerStatus.Expired => TimelineLevel.Milestone,
        _ => TimelineLevel.Detail,
    };

    // The closed status axis is the ONLY thing severity reads: a landed call is Success, a failed/denied one Error, a
    // reaper-expired approval Warning; everything still in flight (Pending / AwaitingApproval / Running) is Info.
    private static TimelineSeverity SeverityFor(ToolCallLedgerStatus status) => status switch
    {
        ToolCallLedgerStatus.Succeeded => TimelineSeverity.Success,
        ToolCallLedgerStatus.Failed => TimelineSeverity.Error,
        ToolCallLedgerStatus.Denied => TimelineSeverity.Error,
        ToolCallLedgerStatus.Expired => TimelineSeverity.Warning,
        _ => TimelineSeverity.Info,
    };
}

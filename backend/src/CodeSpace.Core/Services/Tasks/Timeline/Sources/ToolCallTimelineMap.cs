using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Pure mapping from ONE side-effecting tool-call ledger row (<c>tool_call_ledger</c>) to a narrative timeline event —
/// the "what the agent DID to the world" story line (opened a PR, committed, ran a governed command). Only
/// side-effecting tools get a ledger row (read-only tools never do), so every mapped row is genuinely narrative-worthy.
///
/// <para>The title is OUTCOME-AWARE: a landed call reads the past-tense action ("Opened a pull request"), an in-flight /
/// failed / denied / expired / awaiting-approval call reads the gerund with a status qualifier ("Opening the pull
/// request failed", "… was denied", "… awaiting your approval") — so a side effect that DIDN'T land never reads the same
/// as one that did. The tool KIND stays the (open) provenance on <see cref="RunTimelineEvent.Kind"/>; an unknown tool
/// degrades to the tool-neutral "Called {kind}", never a raw switch. Severity + level ride the CLOSED
/// <see cref="ToolCallLedgerStatus"/> axis. Extracted from the source so the title / summary / severity are
/// unit-testable without a database.</para>
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
            Title = TitleFor(call.Status, call.ToolKind),
            Summary = SummaryFor(call),
            Severity = SeverityFor(call.Status),
            Level = LevelFor(call.Status),
            OccurredAt = call.CreatedDate,    // the ledger's chronological key (there is no Sequence column)
            Order = 0,                        // no per-row monotonic cursor — the same-tick tie-break falls to Id
            NodeId = nodeByAgent.TryGetValue(call.AgentRunId, out var node) ? node : null,
            AgentRunId = call.AgentRunId.ToString(),
            SourceKey = Key,
        };

    // The past-tense DONE form (a landed success) + the gerund DOING form (every non-landed state) for a tool's side
    // effect — an UNKNOWN tool degrades to the tool-neutral "Called {kind}" / "calling {kind}", so an open tool kind
    // still reads legibly and never surfaces a bare switch. This is the ONE place a tool slug becomes human copy.
    private static (string Done, string Doing) Phrasing(string kind) => kind switch
    {
        "git.open_pr" => ("Opened a pull request", "opening the pull request"),
        "git.open_change_set" => ("Opened a change set", "opening the change set"),
        "git.commit" => ("Committed the changes", "committing the changes"),
        "git.push" => ("Pushed the branch", "pushing the branch"),
        "run_command" or "agent.run_command" => ("Ran a command", "running the command"),
        "deploy.trigger" => ("Triggered a deploy", "triggering the deploy"),
        _ => ($"Called {kind}", $"calling {kind}"),
    };

    // The gerund composes with every non-landed status (failed / was denied / awaiting your approval / in-flight) and
    // the DONE form names a landed success — so a call that didn't land never reads the same as one that did.
    private static string TitleFor(ToolCallLedgerStatus status, string kind)
    {
        var (done, doing) = Phrasing(kind);

        return status switch
        {
            ToolCallLedgerStatus.Succeeded => done,
            ToolCallLedgerStatus.Failed => $"{Capitalize(doing)} failed",
            ToolCallLedgerStatus.Denied => $"{Capitalize(doing)} was denied",
            ToolCallLedgerStatus.Expired => $"{Capitalize(doing)} — approval expired unrun",
            ToolCallLedgerStatus.AwaitingApproval => $"{Capitalize(doing)} — awaiting your approval",
            _ => Capitalize(doing),   // Pending / Running — the gerund reads as in-progress
        };
    }

    /// <summary>A failed / denied / expired call surfaces its recorded reason; a landed success surfaces a legible line off its result (the PR ref, the command's summary) when the tool recorded one; everything else carries none.</summary>
    private static string? SummaryFor(ToolCallLedger call) => call.Status switch
    {
        ToolCallLedgerStatus.Succeeded => ReadResultDetail(call.ResultJson),
        ToolCallLedgerStatus.Failed or ToolCallLedgerStatus.Denied or ToolCallLedgerStatus.Expired => call.Error,
        _ => null,
    };

    // A best-effort, TOOL-NEUTRAL read of a landed call's result: surface the first human-legible field the tool
    // recorded (a summary line, a message, a PR url/number, a commit ref) so a success isn't a bare title with no
    // detail. Null when the result carries none / is redacted / is malformed — never a raw blob. Probes a small ordered
    // set of common field names, so a new tool that writes any of them lights up with no per-tool coupling.
    private static readonly string[] DetailFields = { "summary", "message", "html_url", "url", "number", "ref", "sha" };

    private static string? ReadResultDetail(string? resultJson)
    {
        if (string.IsNullOrWhiteSpace(resultJson)) return null;

        try
        {
            var root = JsonDocument.Parse(resultJson).RootElement;

            if (root.ValueKind != JsonValueKind.Object) return null;

            foreach (var field in DetailFields)
                if (root.TryGetProperty(field, out var v) && Legible(v) is { Length: > 0 } text)
                    return field is "number" ? $"#{text}" : text;

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A short human value off a result field — a non-empty string or a number; anything else (object / array / bool / null) is not a legible one-liner.</summary>
    private static string? Legible(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString()?.Trim(),
        JsonValueKind.Number => v.GetRawText(),
        _ => null,
    };

    private static string Capitalize(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];

    // A side effect that DIDN'T LAND — failed, denied, an approval that expired unrun, OR one still BLOCKED on the
    // human's approval — is a story milestone (the operator must see / act on it); a successful / pending / running call
    // is Detail the wave + the agent's own terminal carry.
    private static TimelineLevel LevelFor(ToolCallLedgerStatus status) => status switch
    {
        ToolCallLedgerStatus.Failed => TimelineLevel.Milestone,
        ToolCallLedgerStatus.Denied => TimelineLevel.Milestone,
        ToolCallLedgerStatus.Expired => TimelineLevel.Milestone,
        ToolCallLedgerStatus.AwaitingApproval => TimelineLevel.Milestone,
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

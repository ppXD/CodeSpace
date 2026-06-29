using CodeSpace.Core.Services.Tasks.Phases.Sources.Nodes;
using CodeSpace.Core.Services.Tasks.Phases.Sources.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Phases;

namespace CodeSpace.Core.Services.Sessions.Room;

/// <summary>
/// The PURE narrative engine — turns a run's merged phase list (+ its status / error / pending decisions) into the
/// render-ready turn blocks: the execution-map stepper, the play-by-play narrative, the agent groups, the decision
/// cards, and the failure diagnostic. This is where the backend OWNS the copy + order so the frontend never has to
/// infer them. No DB, no I/O — unit-tested exhaustively.
///
/// The ordering fix lives here: the supervisor source emits the per-decision tape (Order 1M+seq) and the model-authored
/// semantic phases (Order 2M+idx) as two bands, which jumble on a flat board (Spawn before Investigate). The room
/// separates them by ROLE — the authored phases are the MAP (the plan's shape, the tidy node graph), the decision tape
/// is the NARRATIVE (the play-by-play). Each is ordered within its own band, so they never cross.
/// </summary>
public static class RoomNarrative
{
    /// <summary>The map + summary + inner blocks for one turn — everything <see cref="AssistantTurnBlock"/> needs below its header.</summary>
    public sealed record TurnNarrative(string? Summary, ExecutionMapBlock? Map, IReadOnlyList<RoomBlock> Blocks);

    public static TurnNarrative Build(string idPrefix, long seq, IReadOnlyList<RunPhase> phases, WorkflowRunStatus status, string? error, IReadOnlyList<DecisionBlock> decisions)
    {
        var authored = phases.Where(p => p.SourceKey == SupervisorPhaseSource.Key && p.Kind == SupervisorPhaseSource.AuthoredPhaseKind).OrderBy(p => p.Order).ToList();
        var tape = phases.Where(p => p.SourceKey == SupervisorPhaseSource.Key && p.Kind != SupervisorPhaseSource.AuthoredPhaseKind).OrderBy(p => p.Order).ToList();
        var structural = phases.Where(p => p.SourceKey == WorkflowNodePhaseSource.Key).OrderBy(p => p.Order).ToList();

        var map = BuildMap(idPrefix, seq, authored.Count > 0 ? authored : tape.Count > 0 ? tape : structural);

        var narrativePhases = tape.Count > 0 ? tape : structural;

        var blocks = BuildBlocks(idPrefix, seq, narrativePhases, status, error, decisions);

        return new TurnNarrative(SummaryFor(status, tape, structural, error), map, blocks);
    }

    private static ExecutionMapBlock? BuildMap(string idPrefix, long seq, IReadOnlyList<RunPhase> source)
    {
        if (source.Count == 0) return null;

        var steps = source.Select((p, i) => new ExecutionMapStep
        {
            Id = $"{idPrefix}:step-{i}",
            Label = p.Label,
            Status = MapStatus(p.Status),
        }).ToList();

        return new ExecutionMapBlock { Id = $"{idPrefix}:map", Seq = seq, Steps = steps };
    }

    private static IReadOnlyList<RoomBlock> BuildBlocks(string idPrefix, long seq, IReadOnlyList<RunPhase> narrativePhases, WorkflowRunStatus status, string? error, IReadOnlyList<DecisionBlock> decisions)
    {
        var blocks = new List<RoomBlock>();

        foreach (var phase in narrativePhases)
        {
            if (NarrativeLine(phase) is { } line)
                blocks.Add(new NarrativeStepBlock { Id = $"{idPrefix}:{phase.Id}:line", Seq = seq, Text = line, Tone = ToneFor(phase), At = phase.StartedAt });

            if (phase.Agents.Count > 0)
                blocks.Add(new AgentGroupBlock { Id = $"{idPrefix}:{phase.Id}:agents", Seq = seq, Title = GroupTitle(phase), Agents = phase.Agents.Select(ToCard).ToList() });
        }

        // Pending decisions are "now" — the current ask, after the play-by-play.
        blocks.AddRange(decisions);

        if (status is WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled)
            blocks.Add(new DiagnosticBlock { Id = $"{idPrefix}:diagnostic", Seq = seq, Tone = NarrativeTone.Error, Text = FailureText(status, narrativePhases, error) });

        return blocks;
    }

    /// <summary>The turn's one-line headline — substantive model text only: the model's closing summary on success, the
    /// humanized cause on failure. In-progress / waiting return null (the header status word + the flow end cap convey
    /// that), so the lead line is never a generic status echo.</summary>
    private static string? SummaryFor(WorkflowRunStatus status, IReadOnlyList<RunPhase> tape, IReadOnlyList<RunPhase> structural, string? error) => status switch
    {
        WorkflowRunStatus.Success => StopSummary(tape),
        WorkflowRunStatus.Failure or WorkflowRunStatus.Cancelled => FailureText(status, tape.Count > 0 ? tape : structural, error),
        _ => null,
    };

    /// <summary>The stop decision's recorded summary (the model's "here's what I did") — surfaced on the stop phase's <see cref="RunPhase.Summary"/>.</summary>
    private static string? StopSummary(IReadOnlyList<RunPhase> tape) =>
        tape.LastOrDefault(p => p.Kind == SupervisorDecisionKinds.Stop)?.Summary is { Length: > 0 } s ? s : null;

    /// <summary>One humanized play-by-play line for a phase, or null to emit no line (a phase we only want as an agent group / map step).</summary>
    private static string? NarrativeLine(RunPhase phase) => phase.Kind switch
    {
        SupervisorDecisionKinds.Plan => "Planned the approach.",
        SupervisorDecisionKinds.Spawn => phase.Metrics.AgentCount > 0 ? $"Dispatched {AgentWord(phase.Metrics.AgentCount)} to work in parallel." : null,   // a 0-agent spawn is a no-op — no line
        SupervisorDecisionKinds.Retry => "Retried a subtask that needed another pass.",
        SupervisorDecisionKinds.AskHuman => phase.Summary is { Length: > 0 } q ? q : "Asked for input.",
        SupervisorDecisionKinds.Merge => phase.Status == PhaseStatus.Succeeded ? "Merged the agents' work." : "Merging the agents' work.",
        SupervisorDecisionKinds.Resolve => "Resolved a merge conflict.",
        SupervisorDecisionKinds.Stop => null,   // the closing summary is the turn headline, not a duplicated line
        _ => phase.Label,   // a structural node / map / agent phase — its own label is the line
    };

    private static string GroupTitle(RunPhase phase) =>
        phase.Metrics.AgentCount > 1 ? $"{phase.Metrics.AgentCount} agents" : "Agent";

    private static RoomAgentCard ToCard(PhaseAgentRef a) => new()
    {
        AgentRunId = a.AgentRunId,
        Label = a.Label ?? a.AssignedSubtask ?? a.Role ?? "Agent",
        Role = a.Role,
        Status = a.Status,
        Model = a.Model,
        Tokens = a.InputTokens is null && a.OutputTokens is null ? null : (a.InputTokens ?? 0) + (a.OutputTokens ?? 0),
        CostUsd = a.CostUsd,
        FilesChanged = a.FilesChanged,
        Summary = a.Summary,
        LatestLine = null,   // R3 streams the agent's live public activity here
    };

    /// <summary>A readable failure line — the most specific cause available (a failed phase's detail, else the run error stripped of engine jargon), never the raw "Node 'x' failed".</summary>
    private static string FailureText(WorkflowRunStatus status, IReadOnlyList<RunPhase> narrativePhases, string? error)
    {
        if (status == WorkflowRunStatus.Cancelled) return "This turn was cancelled.";

        var phaseDetail = narrativePhases.LastOrDefault(p => p.Status == PhaseStatus.Failed && !string.IsNullOrWhiteSpace(p.Summary))?.Summary;

        return phaseDetail ?? Humanize(error);
    }

    /// <summary>Strip the "Node 'x' failed: " engine prefix to the real cause; fall back to a plain sentence when there's no human detail.</summary>
    private static string Humanize(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "This turn ended with an error.";

        const string marker = "failed: ";
        var idx = error.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        var msg = (idx >= 0 ? error[(idx + marker.Length)..] : error).Trim();

        // Still bare engine jargon (e.g. "Node 'sup' failed" with no detail) → a plain sentence instead.
        return msg.Length == 0 || msg.StartsWith("Node '", StringComparison.OrdinalIgnoreCase) ? "This turn ended with an error." : msg;
    }

    private static string AgentWord(int count) => count == 1 ? "1 agent" : $"{count} agents";

    private static NarrativeTone ToneFor(RunPhase phase) => phase.Status switch
    {
        PhaseStatus.Failed => NarrativeTone.Error,
        PhaseStatus.Succeeded when phase.Kind is SupervisorDecisionKinds.Merge or SupervisorDecisionKinds.Resolve => NarrativeTone.Success,
        _ => NarrativeTone.Info,
    };

    private static ExecutionStepStatus MapStatus(PhaseStatus status) => status switch
    {
        PhaseStatus.Active => ExecutionStepStatus.Running,
        PhaseStatus.Waiting => ExecutionStepStatus.Blocked,
        PhaseStatus.Succeeded => ExecutionStepStatus.Done,
        PhaseStatus.Failed => ExecutionStepStatus.Failed,
        _ => ExecutionStepStatus.Pending,   // Pending / Skipped → neutral
    };
}

using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Pure mapping from ONE supervisor ledger row (<c>supervisor_decision</c>) to a narrative timeline event — the
/// dynamic-supervisor story line (planned · spawned · retried · asked you · merged · resolved · stopped). The verb
/// (<c>DecisionKind</c>) is OPEN — an unknown verb degrades to "Supervisor: {kind}" rather than dropping, so the
/// source stays run-neutral. Severity rides the CLOSED <see cref="SupervisorDecisionStatus"/> axis, never the verb.
/// The ask_human summary surfaces the question (+ the human's answer once folded, mirroring <c>SupervisorPhaseSource</c>);
/// every other verb carries the terminal failure reason. Extracted from the source so the label / severity / summary
/// are unit-testable without a database.
/// </summary>
public static class SupervisorDecisionTimelineMap
{
    /// <summary>The supervisor source's provenance key — stamped on every event this mapper emits.</summary>
    public const string Key = "supervisor";

    /// <summary>The timeline event id (and thus the journal step id) for a decision row — the ONE format both this map and the journal facts sources key by, so an enrichment source can address a decision's step without the format drifting.</summary>
    public static string EventId(SupervisorDecisionRecord d) => $"supervisor-{d.Id:N}";

    public static RunTimelineEvent ToEvent(SupervisorDecisionRecord d) =>
        new()
        {
            Id = EventId(d),
            Kind = $"supervisor.{d.DecisionKind}",
            Title = TitleFor(d),
            Summary = SummaryFor(d),
            Severity = SeverityFor(d.Status),
            Level = TimelineLevel.Milestone,   // every supervisor decision is a story beat — it never folds away
            OccurredAt = d.CreatedDate,
            Order = d.Sequence,   // the ledger's per-run monotonic cursor — the same-OccurredAt tie-break
            SourceKey = Key,
        };

    // A past-tense headline naming the SUPERVISOR as the actor: the merged story line shows no source label and these
    // events carry no agent tag, so the actor lives in the title. Mirrors SupervisorPhaseSource's verb vocabulary.
    private static string TitleFor(SupervisorDecisionRecord d) => d.DecisionKind switch
    {
        SupervisorDecisionKinds.Plan => "Supervisor planned the work",
        SupervisorDecisionKinds.Spawn => SpawnTitle(d),
        SupervisorDecisionKinds.Retry => RetryTitle(d),
        SupervisorDecisionKinds.AskHuman => "Supervisor asked you",
        SupervisorDecisionKinds.Merge => "Supervisor merged the results",
        SupervisorDecisionKinds.Resolve => "Supervisor resolved a conflict",
        SupervisorDecisionKinds.Stop => "Supervisor stopped",
        _ => $"Supervisor: {d.DecisionKind}",
    };

    /// <summary>The spawn headline names the fan-out width when the outcome has staged its agents (a still-pending spawn has none yet → the bare verb).</summary>
    private static string SpawnTitle(SupervisorDecisionRecord d)
    {
        var n = SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson).Count;

        return n switch { 0 => "Supervisor spawned agents", 1 => "Supervisor spawned 1 agent", _ => $"Supervisor spawned {n} agents" };
    }

    /// <summary>A retry that SETTLED having staged NO agent re-ran nothing — the model authored a retry with no (or an unknown) subtask id, so the server no-op'd it. Say so, instead of "retried a subtask", which implies work happened. A still-in-flight retry (not yet settled) keeps the plain verb. Public so the Session room narrative reuses this ONE retry-copy authority.</summary>
    public static string RetryTitle(SupervisorDecisionRecord d) =>
        d.Status == SupervisorDecisionStatus.Succeeded && SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson).Count == 0
            ? "Supervisor reviewed the results — no retry needed"
            : "Supervisor retried a subtask";

    // A finished decision is Success, a failed one Error, a reaper-expired one Warning; everything still in flight
    // (Pending / AwaitingApproval / Running) is Info. The closed status axis is the ONLY thing severity reads.
    private static TimelineSeverity SeverityFor(SupervisorDecisionStatus status) => status switch
    {
        SupervisorDecisionStatus.Succeeded => TimelineSeverity.Success,
        SupervisorDecisionStatus.Failed => TimelineSeverity.Error,
        SupervisorDecisionStatus.Expired => TimelineSeverity.Warning,
        _ => TimelineSeverity.Info,
    };

    /// <summary>ask_human → the question joined with the human's answer once folded (falls back to the error when the outcome holds no question); every other verb → the terminal failure reason (null while it succeeded / is in flight).</summary>
    private static string? SummaryFor(SupervisorDecisionRecord d)
    {
        if (d.DecisionKind != SupervisorDecisionKinds.AskHuman) return d.Error;

        var question = SupervisorOutcome.ReadAskHumanQuestion(d.OutcomeJson);

        if (question == null) return d.Error;

        var answer = SupervisorOutcome.ReadAskHumanAnswer(d.OutcomeJson);

        return answer == null ? question : $"{question} — {answer}";
    }
}

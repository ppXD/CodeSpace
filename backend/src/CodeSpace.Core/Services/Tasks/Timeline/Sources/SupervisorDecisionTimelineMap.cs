using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks.Timeline;

namespace CodeSpace.Core.Services.Tasks.Timeline.Sources;

/// <summary>
/// Pure mapping from ONE supervisor ledger row (<c>supervisor_decision</c>) to a narrative timeline event — the
/// dynamic-supervisor story line (planned · spawned · retried · asked you · merged · resolved · stopped). The verb
/// (<c>DecisionKind</c>) is OPEN — an unknown verb degrades to "Supervisor: {kind}" rather than dropping, so the
/// source stays run-neutral.
///
/// <para>Every verb's title, summary AND tone are OUTCOME-AWARE, read off the recorded outcome via the
/// <c>SupervisorOutcome</c> readers — the plan names its subtask count, a spawn that staged nothing says so (not a
/// bland "spawned agents"), a merge distinguishes clean vs conflicted, a resolve verified vs needs-review, and a
/// stop distinguishes a genuine success vs a model give-up vs a server-forced bound (naming WHICH bound). Severity
/// still rides the CLOSED <see cref="SupervisorDecisionStatus"/> axis, but a SUCCEEDED decision whose OUTCOME is
/// degraded (conflicted/failed merge, unverified resolution, give-up/forced stop) reads amber, not green — so a
/// "successful" decision that produced a poor outcome never misleads. The stop verdict is the SAME
/// <see cref="SupervisorStopClassification"/> the RESULT card reads, so the step and the terminal can't drift.
/// Extracted from the source so the label / severity / summary are unit-testable without a database.</para>
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
            Severity = SeverityFor(d),
            Level = TimelineLevel.Milestone,   // every supervisor decision is a story beat — it never folds away
            OccurredAt = d.CreatedDate,
            Order = d.Sequence,   // the ledger's per-run monotonic cursor — the same-OccurredAt tie-break
            SourceKey = Key,
        };

    // A past-tense headline naming the SUPERVISOR as the actor: the merged story line shows no source label and these
    // events carry no agent tag, so the actor lives in the title. Each verb reads its OWN outcome so the headline
    // reflects what actually happened, not just that the verb ran. Mirrors SupervisorPhaseSource's verb vocabulary.
    private static string TitleFor(SupervisorDecisionRecord d) => d.DecisionKind switch
    {
        SupervisorDecisionKinds.Plan => PlanTitle(d),
        SupervisorDecisionKinds.Spawn => SpawnTitle(d),
        SupervisorDecisionKinds.Retry => RetryTitle(d),
        SupervisorDecisionKinds.AskHuman => "Supervisor asked you",
        SupervisorDecisionKinds.Merge => MergeTitle(d),
        SupervisorDecisionKinds.Resolve => ResolveTitle(d),
        SupervisorDecisionKinds.Stop => StopTitle(d),
        _ => $"Supervisor: {d.DecisionKind}",
    };

    /// <summary>The plan headline names the subtask count the supervisor authored (0 / unreadable → the bare verb, so a pre-plan or malformed row still reads sensibly).</summary>
    private static string PlanTitle(SupervisorDecisionRecord d) =>
        SupervisorOutcome.ReadPlanSubtasks(d.PayloadJson).Count switch
        {
            0 => "Supervisor planned the work",
            1 => "Supervisor planned 1 subtask",
            var n => $"Supervisor planned {n} subtasks",
        };

    /// <summary>The spawn headline names the fan-out width. A SETTLED spawn that staged NO agent dispatched nothing — say so ("spawned no agents"), instead of "spawned agents", which implies work started; a still-pending spawn keeps the bare verb (it may yet stage). Mirrors <see cref="RetryTitle"/>'s settled-no-op precedent.</summary>
    private static string SpawnTitle(SupervisorDecisionRecord d)
    {
        var n = SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson).Count;

        if (n == 0)
            return d.Status == SupervisorDecisionStatus.Succeeded ? "Supervisor spawned no agents" : "Supervisor spawned agents";

        return n == 1 ? "Supervisor spawned 1 agent" : $"Supervisor spawned {n} agents";
    }

    /// <summary>A retry that SETTLED having staged NO agent re-ran nothing — the model authored a retry with no (or an unknown) subtask id, so the server no-op'd it. Say so, instead of "retried a subtask", which implies work happened. A still-in-flight retry (not yet settled) keeps the plain verb. Public so the Session room narrative reuses this ONE retry-copy authority.</summary>
    public static string RetryTitle(SupervisorDecisionRecord d) =>
        d.Status == SupervisorDecisionStatus.Succeeded && SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson).Count == 0
            ? "Supervisor reviewed the results — no retry needed"
            : "Supervisor retried a subtask";

    /// <summary>The merge headline reflects the on-disk integration: a clean/skipped integration merged the results; a conflicted one names the file count; a git-infra failure says the merge failed. No integration block (in flight / unreadable) → the bare verb.</summary>
    private static string MergeTitle(SupervisorDecisionRecord d)
    {
        var integration = SupervisorOutcome.ReadIntegration(d.OutcomeJson);

        if (integration == null) return "Supervisor merged the results";

        if (integration.IsConflicted)
        {
            var n = integration.ConflictedFiles.Count;
            return n == 0 ? "Supervisor hit a merge conflict" : $"Supervisor hit a merge conflict in {n} file{(n == 1 ? "" : "s")}";
        }

        return IsIntegrationFailed(integration) ? "Supervisor's merge failed" : "Supervisor merged the results";
    }

    /// <summary>The resolve headline reflects the resolver agent's build/test verdict: a verified reconciliation resolved the conflict; an unverified one needs review; an unknown/in-flight verdict keeps the bare verb.</summary>
    private static string ResolveTitle(SupervisorDecisionRecord d) => SupervisorOutcome.ReadResolutionVerdict(d.OutcomeJson) switch
    {
        SupervisorResolutionVerdict.Verified => "Supervisor resolved the conflict",
        SupervisorResolutionVerdict.Unverified => "Supervisor's resolution needs review",
        _ => "Supervisor resolved a conflict",
    };

    /// <summary>The stop headline reflects the SAME terminal classification the RESULT card reads: a server-FORCED stop names the bound that stopped it ("— budget exhausted"), a model GIVE-UP reads "stopped early", a genuine success (or bare/unclassifiable stop) reads the neutral verb — the green tone + the summary carry the success signal.</summary>
    private static string StopTitle(SupervisorDecisionRecord d) => SupervisorOutcome.ClassifyStop(d.PayloadJson, d.OutcomeJson) switch
    {
        { Kind: SupervisorStopKind.Forced, Reason: { } reason } => $"Supervisor stopped — {reason}",
        { Kind: SupervisorStopKind.GaveUp } => "Supervisor stopped early",
        _ => "Supervisor stopped",
    };

    // Severity rides the CLOSED status axis — a finished decision is Success, a failed one Error, a reaper-expired one
    // Warning, everything in flight Info — but a SUCCEEDED decision whose OUTCOME is degraded reads amber, not green,
    // so a "successful" decision that produced a poor outcome (a conflicted merge, an unverified resolution, a
    // give-up / forced stop) never reads as an unqualified win.
    private static TimelineSeverity SeverityFor(SupervisorDecisionRecord d)
    {
        var status = StatusSeverity(d.Status);

        return status == TimelineSeverity.Success && HasDegradedOutcome(d) ? TimelineSeverity.Warning : status;
    }

    private static TimelineSeverity StatusSeverity(SupervisorDecisionStatus status) => status switch
    {
        SupervisorDecisionStatus.Succeeded => TimelineSeverity.Success,
        SupervisorDecisionStatus.Failed => TimelineSeverity.Error,
        SupervisorDecisionStatus.Expired => TimelineSeverity.Warning,
        _ => TimelineSeverity.Info,
    };

    /// <summary>A decision whose STATUS succeeded but whose OUTCOME is degraded — a conflicted/failed merge, an unverified resolution, or a give-up/forced stop. Every other verb's status IS its outcome (a spawn either staged or didn't; a plan either planned or failed), so they never downgrade.</summary>
    private static bool HasDegradedOutcome(SupervisorDecisionRecord d) => d.DecisionKind switch
    {
        SupervisorDecisionKinds.Merge => IsMergeDegraded(d),
        SupervisorDecisionKinds.Resolve => SupervisorOutcome.ReadResolutionVerdict(d.OutcomeJson) == SupervisorResolutionVerdict.Unverified,
        SupervisorDecisionKinds.Stop => SupervisorOutcome.ClassifyStop(d.PayloadJson, d.OutcomeJson).Degraded,
        _ => false,
    };

    private static bool IsMergeDegraded(SupervisorDecisionRecord d)
    {
        var integration = SupervisorOutcome.ReadIntegration(d.OutcomeJson);

        return integration != null && (integration.IsConflicted || IsIntegrationFailed(integration));
    }

    private static bool IsIntegrationFailed(SupervisorIntegrationOutcome integration) => string.Equals(integration.Status, "Failed", StringComparison.OrdinalIgnoreCase);

    // Each verb's summary carries its OUTCOME detail — the guidance a bare "it happened" line lacked — falling back to
    // the terminal failure reason (d.Error) when the outcome holds no detail, so a FAILED decision still surfaces why.
    private static string? SummaryFor(SupervisorDecisionRecord d) => d.DecisionKind switch
    {
        SupervisorDecisionKinds.AskHuman => AskHumanSummary(d),
        SupervisorDecisionKinds.Spawn => SpawnSummary(d) ?? d.Error,
        SupervisorDecisionKinds.Merge => MergeSummary(d) ?? d.Error,
        SupervisorDecisionKinds.Resolve => ResolveSummary(d) ?? d.Error,
        SupervisorDecisionKinds.Stop => StopSummary(d) ?? d.Error,
        _ => d.Error,
    };

    /// <summary>ask_human → the question joined with the human's answer once folded (falls back to the error when the outcome holds no question).</summary>
    private static string? AskHumanSummary(SupervisorDecisionRecord d)
    {
        var question = SupervisorOutcome.ReadAskHumanQuestion(d.OutcomeJson);

        if (question == null) return d.Error;

        var answer = SupervisorOutcome.ReadAskHumanAnswer(d.OutcomeJson);

        return answer == null ? question : $"{question} — {answer}";
    }

    /// <summary>A settled spawn that dispatched NO agent states the fact — nothing ran this round — the guidance the empty-spawn beat otherwise lacked. Deliberately does NOT claim WHY (the model may author an empty set even with work ready; any blocked-on-dependency detail rides its own Deferred facts). A spawn that staged agents carries none (the agent cards ARE the detail).</summary>
    private static string? SpawnSummary(SupervisorDecisionRecord d) =>
        d.Status == SupervisorDecisionStatus.Succeeded && SupervisorOutcome.ReadStagedAgentRunIds(d.OutcomeJson).Count == 0
            ? "No agent was dispatched this round — the supervisor staged no subtask to run."
            : null;

    /// <summary>A conflicted merge names the conflicting files (or its reason); a failed merge carries its git-infra reason; a clean/skipped merge carries none.</summary>
    private static string? MergeSummary(SupervisorDecisionRecord d)
    {
        var integration = SupervisorOutcome.ReadIntegration(d.OutcomeJson);

        if (integration == null) return null;

        if (integration.IsConflicted)
            return integration.ConflictedFiles.Count == 0
                ? integration.Reason ?? "The agents' work conflicted while integrating."
                : $"Conflicted while integrating: {string.Join(", ", integration.ConflictedFiles)}";

        return IsIntegrationFailed(integration) ? integration.Reason : null;
    }

    /// <summary>An unverified resolution explains the build/tests didn't pass on the reconciliation; a verified / in-flight resolve carries none.</summary>
    private static string? ResolveSummary(SupervisorDecisionRecord d) =>
        SupervisorOutcome.ReadResolutionVerdict(d.OutcomeJson) == SupervisorResolutionVerdict.Unverified
            ? "The reconciliation wasn't verified — the build or tests didn't pass on the resolved result."
            : null;

    /// <summary>The stop's closing line — the model's summary on a success / give-up, or the bound reason on a server-forced stop (so a forced stop never renders a blank summary). Null when the stop carries neither signal.</summary>
    private static string? StopSummary(SupervisorDecisionRecord d) => SupervisorOutcome.ClassifyStop(d.PayloadJson, d.OutcomeJson).DisplayText;
}

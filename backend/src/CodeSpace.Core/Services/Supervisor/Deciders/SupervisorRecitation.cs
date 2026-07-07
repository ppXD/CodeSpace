using System.Text;
using CodeSpace.Messages.Agents;

namespace CodeSpace.Core.Services.Supervisor.Deciders;

/// <summary>
/// The plan RECITATION block (triad S8, the Manus lesson): a compact restatement of the CURRENT plan with each
/// item's LIVE state, re-derived from the tape every turn and injected at the PROMPT TAIL — the recency-biased
/// position — so a long-running supervisor never loses the plan under a growing prior-decision log. Pure over
/// <see cref="SupervisorPriorDecision"/>s (the same durable rows every other read derives from — never a second
/// source of truth): the newest plan's subtasks, each joined to its LATEST covering spawn/retry attempt's folded
/// result (positional subtaskIds[i] ↔ results[i], the platform's standard join). Null when no plan exists — a
/// planless run's prompt stays byte-identical.
/// </summary>
public static class SupervisorRecitation
{
    /// <summary>The block's pinned header — the recitation is a stable prompt landmark (tests + the model key on it).</summary>
    public const string Header = "CURRENT PLAN STATE (recite before deciding — unfinished items are the remaining work):";

    public static string? Render(IReadOnlyList<SupervisorPriorDecision> priorDecisions)
    {
        var subtasks = LatestPlanSubtasks(priorDecisions);

        if (subtasks.Count == 0) return null;

        var builder = new StringBuilder(Header);
        var unfinished = new List<string>();

        foreach (var subtask in subtasks)
        {
            var state = StateFor(subtask.Id, priorDecisions);

            builder.AppendLine().Append($"- [{subtask.Id}] {subtask.Title}: {state}");

            // Authoring LINT, recited every turn until fixed (the free tier-0 check the supervisor lane's plans
            // never got): a half-authored acceptance spec (judge without rubric, schema check without schema) can
            // NEVER pass at grade time — telling the model NOW turns a paid clone + a fail-closed verdict + a retry
            // temptation into one re-plan. Pure over the authored spec; a valid/absent spec adds nothing.
            if (subtask.Acceptance is { } spec && Agents.AgentAcceptanceContract.ValidateAuthored(spec) is { } specError)
                builder.Append($" ⚠ its acceptance spec is INVALID as authored ({specError}) — it can never pass; re-plan this item's check.");

            if (state is not ("done (accepted)" or "done")) unfinished.Add(subtask.Id);
        }

        builder.AppendLine().Append(unfinished.Count == 0
            ? "Every plan item is finished — merge the results and drive to a verified stop."
            : $"Unfinished: {string.Join(", ", unfinished)}.");

        return builder.ToString();
    }

    /// <summary>The newest plan decision's subtasks — a re-plan supersedes (the same newest-plan rule the acceptance fold uses).</summary>
    private static IReadOnlyList<SupervisorPlannedSubtask> LatestPlanSubtasks(IReadOnlyList<SupervisorPriorDecision> priors)
    {
        for (var i = priors.Count - 1; i >= 0; i--)
            if (priors[i].DecisionKind == SupervisorDecisionKinds.Plan)
                return SupervisorOutcome.ReadPlanSubtasks(priors[i].PayloadJson);

        return Array.Empty<SupervisorPlannedSubtask>();
    }

    /// <summary>
    /// One subtask's live state off its LATEST covering spawn/retry: the folded result's status + acceptance verdict
    /// (accepted / REJECTED-with-detail / failed-with-error / the raw non-terminal status), a staged-but-unfolded
    /// attempt reads "running", and an un-staged subtask "pending". Newest-first scan, so a retry supersedes the
    /// original spawn — exactly the freshest-attempt rule the decider prompt already marks.
    /// </summary>
    internal static string StateFor(string subtaskId, IReadOnlyList<SupervisorPriorDecision> priors)
    {
        for (var i = priors.Count - 1; i >= 0; i--)
        {
            var decision = priors[i];

            if (!SupervisorDecisionKinds.StagesAgents(decision.DecisionKind)) continue;

            var ids = SupervisorOutcome.ReadSpawnSubtaskIds(decision.PayloadJson);
            var index = IndexOf(ids, subtaskId);

            if (index < 0) continue;

            var results = SupervisorOutcome.ReadAgentResults(decision.OutcomeJson);

            if (index >= results.Count) return "running";   // staged, outcome not folded yet

            return Describe(results[index]);
        }

        return "pending";
    }

    private static string Describe(SupervisorAgentResult result) => result.Status switch
    {
        "Succeeded" when result.AcceptancePassed == true => "done (accepted)",
        // Same three-way split as the decider's verdict line — the recitation and the results section must never
        // give the weak brain CONTRADICTORY framings of the same row (one says REJECTED-retry, the other UNVERIFIED-replan).
        "Succeeded" when result.AcceptancePassed == false && IsInfraRejection(result) => $"done but its check COULD NOT RUN ({Truncate(result.AcceptanceDetail)}) — re-plan the check, do not retry the agent",
        "Succeeded" when result.AcceptancePassed == false => $"done but REJECTED by its acceptance check ({Truncate(result.AcceptanceDetail)})",
        "Succeeded" => "done",
        "Failed" when result.AcceptancePassed == false && IsInfraRejection(result) => $"done but its check COULD NOT RUN ({Truncate(result.AcceptanceDetail)}) — re-plan the check, do not retry the agent",
        "Failed" => $"failed ({Truncate(result.Error ?? result.AcceptanceDetail)})",
        var other => (other ?? "running").ToLowerInvariant(),
    };

    /// <summary>The shared infra classification over the compact — the SAME split (and the SAME work-present read) the decider's verdict line renders, so the two prompt sections can't disagree about a row.</summary>
    private static bool IsInfraRejection(SupervisorAgentResult result) =>
        Agents.AgentAcceptanceContract.IsInfraFailure(result.AcceptanceDetail, SupervisorOutcome.ResultShowsWork(result));

    private static int IndexOf(IReadOnlyList<string> ids, string subtaskId)
    {
        for (var i = 0; i < ids.Count; i++)
            if (string.Equals(ids[i], subtaskId, StringComparison.Ordinal)) return i;

        return -1;
    }

    private static string Truncate(string? detail) =>
        string.IsNullOrWhiteSpace(detail) ? "no detail" : detail.Length <= 160 ? detail : detail[..160] + "…";
}

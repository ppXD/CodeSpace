using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// The S8 recitation block — pure over the prior-decision tape: the NEWEST plan's items each joined (positionally)
/// to their LATEST covering spawn/retry result, rendered with live states + an explicit unfinished list at the
/// prompt tail. No plan ⇒ null ⇒ the prompt stays byte-identical.
/// </summary>
[Trait("Category", "Unit")]
public sealed class SupervisorRecitationTests
{
    [Fact]
    public void No_plan_renders_nothing() =>
        SupervisorRecitation.Render(new[] { Prior(1, SupervisorDecisionKinds.AskHuman, "{}") }).ShouldBeNull();

    [Fact]
    public void Items_join_their_latest_attempt_and_the_unfinished_list_names_the_remaining_work()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First"), ("s2", "Second")),
            Spawn(2, subtaskIds: new[] { "s1", "s2" },
                Result("Succeeded", acceptancePassed: true),
                Result("Failed", error: "exit 1")),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldStartWith(SupervisorRecitation.Header);
        recitation.ShouldContain("- [s1] First: done (accepted)");
        recitation.ShouldContain("- [s2] Second: failed (exit 1)");
        recitation.ShouldContain("Unfinished: s2.");
    }

    [Fact]
    public void A_retry_supersedes_the_original_spawn()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Failed", error: "flaked")),
            Spawn(3, new[] { "s1" }, Result("Succeeded", acceptancePassed: true)),   // the retry attempt
        };

        SupervisorRecitation.Render(priors)!.ShouldContain("- [s1] First: done (accepted)", customMessage: "the freshest attempt wins — exactly the rule the decider prompt marks");
    }

    [Fact]
    public void A_rejected_unit_recites_its_acceptance_detail_and_a_replan_supersedes_the_old_plan()
    {
        var priors = new[]
        {
            Plan(1, ("old", "Old item")),
            Plan(2, ("s1", "Fresh item")),
            Spawn(3, new[] { "s1" }, Result("Succeeded", acceptancePassed: false, acceptanceDetail: "rubric 0.50 < 1.00")),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldNotContain("Old item", customMessage: "a re-plan supersedes — the recitation restates the CURRENT plan only");
        recitation.ShouldContain("REJECTED by its acceptance check (rubric 0.50 < 1.00)");
        recitation.ShouldContain("Unfinished: s1.");
    }

    [Fact]
    public void Staged_but_unfolded_reads_running_and_unstaged_reads_pending()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First"), ("s2", "Second")),
            Spawn(2, new[] { "s1" } /* no folded results yet */),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("- [s1] First: running");
        recitation.ShouldContain("- [s2] Second: pending");
    }

    [Fact]
    public void An_all_finished_plan_steers_toward_merge_and_stop()
    {
        var priors = new[]
        {
            Plan(1, ("s1", "First")),
            Spawn(2, new[] { "s1" }, Result("Succeeded", acceptancePassed: true)),
        };

        SupervisorRecitation.Render(priors)!.ShouldContain("Every plan item is finished");
    }

    // ─── fixtures ────────────────────────────────────────────────────────────

    private static SupervisorPriorDecision Plan(int seq, params (string Id, string Title)[] subtasks) =>
        Prior(seq, SupervisorDecisionKinds.Plan, JsonSerializer.Serialize(new
        {
            subtasks = subtasks.Select(s => new { id = s.Id, title = s.Title, instruction = $"do {s.Id}" }).ToArray(),
        }, AgentJson.Options));

    private static SupervisorPriorDecision Spawn(int seq, string[] subtaskIds, params object[] results) =>
        Prior(seq, SupervisorDecisionKinds.Spawn,
            JsonSerializer.Serialize(new { subtaskIds }, AgentJson.Options),
            results.Length == 0 ? null : JsonSerializer.Serialize(new { agentResults = results }, AgentJson.Options));

    private static object Result(string status, bool? acceptancePassed = null, string? error = null, string? acceptanceDetail = null) =>
        new { agentRunId = Guid.NewGuid(), status, error, acceptancePassed, acceptanceDetail };

    private static SupervisorPriorDecision Prior(int seq, string kind, string payloadJson, string? outcomeJson = null) => new()
    {
        Id = Guid.NewGuid(),
        Sequence = seq,
        Status = SupervisorDecisionStatus.Succeeded,
        DecisionKind = kind,
        PayloadJson = payloadJson,
        OutcomeJson = outcomeJson,
    };
}

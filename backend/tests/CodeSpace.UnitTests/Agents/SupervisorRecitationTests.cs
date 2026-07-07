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

    [Fact]
    public void An_infra_classed_rejection_recites_could_not_run_never_rejected()
    {
        // P0: the recitation and the decider's verdict line must give the SAME framing — a check that could not RUN
        // (publish failed, work present) is not a rejection of the work, and the recited steer is re-plan, not retry.
        var priors = new[]
        {
            Plan(1, ("s1", "Report")),
            Prior(2, SupervisorDecisionKinds.Spawn, JsonSerializer.Serialize(new { subtaskIds = new[] { "s1" } }, AgentJson.Options),
                JsonSerializer.Serialize(new { agentResults = new object[] { new { agentRunId = Guid.NewGuid(), status = "Succeeded", changedFiles = new[] { "report.md" }, acceptancePassed = false, acceptanceDetail = "no-branch-or-repo" } } }, AgentJson.Options)),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("its check COULD NOT RUN (no-branch-or-repo)");
        recitation.ShouldContain("re-plan the check, do not retry the agent");
        recitation.ShouldNotContain("REJECTED", customMessage: "an unrunnable check must never read as a rejection of the work");
    }

    [Fact]
    public void A_half_authored_acceptance_spec_is_linted_in_the_recitation()
    {
        // P0: the free tier-0 authoring check the supervisor lane's plans never got — a judge without a rubric can
        // NEVER pass at grade time; recite the lint every turn until the model re-plans the check, instead of paying
        // a clone + a fail-closed verdict + a retry temptation.
        var priors = new[]
        {
            Prior(1, SupervisorDecisionKinds.Plan, JsonSerializer.Serialize(new
            {
                subtasks = new object[]
                {
                    new { id = "s1", title = "Report", instruction = "write it", acceptance = new { kind = "LlmJudge", command = new[] { "report.md" } } },
                    new { id = "s2", title = "Sound", instruction = "do it", acceptance = new { command = new[] { "dotnet", "test" } } },
                },
            }, AgentJson.Options)),
        };

        var recitation = SupervisorRecitation.Render(priors)!;

        recitation.ShouldContain("acceptance spec is INVALID as authored", customMessage: "the lint recites until fixed");
        recitation.ShouldContain("re-plan this item's check", customMessage: "the steer: fix the CHECK before spawning");
        recitation.ShouldContain("- [s2] Sound: pending");
        recitation.ShouldNotContain("[s2] Sound: pending ⚠", customMessage: "a valid spec lints nothing");
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

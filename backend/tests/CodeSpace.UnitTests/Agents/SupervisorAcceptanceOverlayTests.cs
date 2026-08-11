using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: B3's co-sign overlay — the one chokepoint that turns APPROVED amend cards into the run's effective
/// oracle view. Pins the safety rulings by name: authority is pairwise off the card's OWN answer (FATAL-2 — an
/// unanswered, redirected, or foreign card applies nothing); anchoring is newest-plan (MAJOR-8 — a re-plan
/// invalidates every earlier amendment); an approved-but-invalid replacement fails CLOSED to the ORIGINAL oracle
/// (MAJOR-4 — never a silent drop to ungraded); cards apply in sequence order so the latest per subtask wins
/// (MINOR-9 determinism). Pure over the tape — a replay re-derives the identical view.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAcceptanceOverlayTests
{
    private static readonly SupervisorAcceptanceSpec Original = new() { Command = new[] { "sh", "check.sh" } };
    private static readonly SupervisorAcceptanceSpec Replacement = new() { Command = new[] { "sh", "verify.sh" } };

    private static SupervisorAmendAcceptancePayload Waive(string id = "s1") =>
        new() { SubtaskId = id, Waive = true, Reason = "oracle names missing tooling" };

    private static SupervisorAmendAcceptancePayload Amend(SupervisorAcceptanceSpec spec, string id = "s1") =>
        new() { SubtaskId = id, Waive = false, Acceptance = spec, Reason = "oracle names missing tooling" };

    private static SupervisorPriorDecision Card(SupervisorAmendAcceptancePayload payload, string? answer, int seq)
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(payload);
        var outcome = answer is null ? "{}" : JsonSerializer.Serialize(new { question = "q", answer }, AgentJson.Options);

        return new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = seq, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.AskHuman, PayloadJson = card.PayloadJson, OutcomeJson = outcome };
    }

    private static SupervisorPriorDecision Plan(int seq) =>
        new() { Id = Guid.NewGuid(), Sequence = seq, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Plan, PayloadJson = "{}", OutcomeJson = "{}" };

    private static IReadOnlyDictionary<string, SupervisorAcceptanceSpec> Planned() =>
        new Dictionary<string, SupervisorAcceptanceSpec> { ["s1"] = Original };

    [Fact]
    public void An_approved_waive_removes_the_oracle_and_marks_the_subtask()
    {
        var view = SupervisorAcceptanceOverlay.Resolve(new[] { Plan(1), Card(Waive(), "approve", 2) }, Planned());

        view.BySubtask.ShouldNotContainKey("s1");
        view.WaivedSubtaskIds.ShouldBe(new HashSet<string> { "s1" });
    }

    [Fact]
    public void An_approved_amendment_replaces_the_oracle()
    {
        var view = SupervisorAcceptanceOverlay.Resolve(new[] { Plan(1), Card(Amend(Replacement), "approve — the new check is right", 2) }, Planned());

        view.BySubtask["s1"].Command.ShouldBe(new[] { "sh", "verify.sh" });
        view.WaivedSubtaskIds.ShouldBeEmpty();
    }

    [Fact]
    public void An_approved_amendment_can_add_an_oracle_to_a_subtask_that_had_none()
    {
        var view = SupervisorAcceptanceOverlay.Resolve(new[] { Plan(1), Card(Amend(Replacement, id: "s2"), "approve", 2) }, Planned());

        view.BySubtask["s2"].Command.ShouldBe(new[] { "sh", "verify.sh" }, "a no-oracle unit is FIXABLE via amend — strictly better than waiving it");
        view.BySubtask["s1"].ShouldBe(Original, "untargeted subtasks keep the plan's own spec");
    }

    [Theory]
    [InlineData(null)]          // unanswered (degraded / still parked) — no authority
    [InlineData("reject")]      // explicit redirect
    [InlineData("use the original check please")]   // guidance, not an approval
    public void A_card_without_its_own_approving_answer_applies_nothing(string? answer)
    {
        var view = SupervisorAcceptanceOverlay.Resolve(new[] { Plan(1), Card(Waive(), answer, 2) }, Planned());

        view.BySubtask["s1"].ShouldBe(Original, "FATAL-2: authority is the card's OWN resolved answer — nothing else");
        view.WaivedSubtaskIds.ShouldBeEmpty();
    }

    [Fact]
    public void A_re_plan_invalidates_every_earlier_amendment()
    {
        var view = SupervisorAcceptanceOverlay.Resolve(new[] { Plan(1), Card(Waive(), "approve", 2), Plan(3) }, Planned());

        view.BySubtask["s1"].ShouldBe(Original, "MAJOR-8: newest-plan anchoring — a plan-v1 waiver must never attach to a re-used id in v2");
        view.WaivedSubtaskIds.ShouldBeEmpty();
    }

    [Fact]
    public void An_approved_but_invalid_replacement_keeps_the_original_oracle()
    {
        var judgeWithoutRubric = new SupervisorAcceptanceSpec { Command = new[] { "report.md" }, Kind = BenchmarkGradingKind.LlmJudge };

        var view = SupervisorAcceptanceOverlay.Resolve(new[] { Plan(1), Card(Amend(judgeWithoutRubric), "approve", 2) }, Planned());

        view.BySubtask["s1"].ShouldBe(Original, "MAJOR-4 fail-closed: an invalid approved spec leaves the ORIGINAL oracle, never drops the unit to ungraded");
        view.WaivedSubtaskIds.ShouldBeEmpty();
    }

    [Fact]
    public void The_latest_approved_amendment_per_subtask_wins()
    {
        var view = SupervisorAcceptanceOverlay.Resolve(
            new[] { Plan(1), Card(Waive(), "approve", 2), Card(Amend(Replacement), "approve", 3) }, Planned());

        view.BySubtask["s1"].Command.ShouldBe(new[] { "sh", "verify.sh" }, "sequence order applies — the later amendment supersedes the earlier waive");
        view.WaivedSubtaskIds.ShouldBeEmpty();

        var reversed = SupervisorAcceptanceOverlay.Resolve(
            new[] { Plan(1), Card(Amend(Replacement), "approve", 2), Card(Waive(), "approve", 3) }, Planned());

        reversed.WaivedSubtaskIds.ShouldBe(new HashSet<string> { "s1" }, "and a later waive supersedes the earlier amendment");
        reversed.BySubtask.ShouldNotContainKey("s1");
    }

    [Fact]
    public void An_ordinary_answered_content_ask_applies_nothing()
    {
        var contentAsk = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.AskHuman,
            PayloadJson = """{"question":"which db?"}""", OutcomeJson = """{"question":"which db?","answer":"approve postgres"}""",
        };

        SupervisorAcceptanceOverlay.Resolve(new[] { Plan(1), contentAsk }, Planned())
            .BySubtask["s1"].ShouldBe(Original, "only a marker-and-payload amend card carries amendment authority");
    }

    [Fact]
    public void A_tape_with_no_amendments_is_the_planned_map_verbatim()
    {
        var view = SupervisorAcceptanceOverlay.Resolve(new[] { Plan(1) }, Planned());

        view.BySubtask["s1"].ShouldBe(Original);
        view.WaivedSubtaskIds.ShouldBeEmpty();
    }

    // ── the spawn-side chokepoint reads the SAME overlay ──────────────────────────────────────────────

    [Fact]
    public void The_spawn_side_planned_subtasks_carry_the_effective_specs()
    {
        // B0's second-overlay-point finding: an amend ADDING a spec must flip the F4 forced-push opt-in for the
        // very retry that will be graded against it — the spawn path must resolve through the SAME overlay as the
        // fold, or the retry runs with push OFF and fails "no-branch-or-repo".
        var planPayload = JsonSerializer.Serialize(new
        {
            goal = "g",
            subtasks = new object[]
            {
                new { id = "s1", title = "t1", instruction = "do 1", acceptance = new { command = new[] { "sh", "check.sh" } } },
                new { id = "s2", title = "t2", instruction = "do 2" },
            },
        }, AgentJson.Options);

        var plan = new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, Status = SupervisorDecisionStatus.Succeeded, DecisionKind = SupervisorDecisionKinds.Plan, PayloadJson = planPayload, OutcomeJson = "{}" };
        var waiveS1 = Card(Waive("s1"), "approve", 2);
        var addS2 = Card(Amend(Replacement, id: "s2"), "approve", 3);

        var context = new SupervisorTurnContext { Goal = "g", PriorDecisions = new[] { plan, waiveS1, addS2 } };

        var subtasks = Core.Services.Supervisor.Executors.RealSupervisorActionExecutor.ResolvePlannedSubtasks(context);

        subtasks["s1"].Acceptance.ShouldBeNull("the waived subtask no longer forces the push opt-in — no oracle will grade it");
        subtasks["s2"].Acceptance!.Command.ShouldBe(new[] { "sh", "verify.sh" }, "the amendment ADDING a spec reaches the spawn path — F4 forces push ON for the graded retry");
    }
}

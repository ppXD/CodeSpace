using CodeSpace.Core.Services.Sessions.Journal.FactsSources;
using CodeSpace.Core.Services.Tasks.Timeline.Sources;
using CodeSpace.Messages.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Shouldly;

namespace CodeSpace.UnitTests.Sessions.Journal;

/// <summary>
/// 🟢 Unit: the ask-answer facts source — attaches the operator's ANSWER to the ASK_HUMAN decision step, keyed by its
/// timeline event id, as its OWN structured field (not folded into the question prose) so the frontend renders the
/// decision distinctly and unambiguously. Pins that ONLY an answered ask_human decision contributes, keyed to its own
/// step; a still-pending (unanswered) ask and a non-ask verb add nothing. Over the shared in-memory decision log — no DB.
/// </summary>
[Trait("Category", "Unit")]
public class AskAnswerFactsSourceTests
{
    [Fact]
    public async Task Keys_the_answer_by_the_ask_decision_step_id()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.AskHuman, "{}", AnswerOutcome("需要精簡一點,太複雜了"));

        var facts = await new AskAnswerFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var decision = log.Rows.Single();
        facts.ShouldContainKey(SupervisorDecisionTimelineMap.EventId(decision));
        facts[SupervisorDecisionTimelineMap.EventId(decision)].Answer.ShouldBe("需要精簡一點,太複雜了", "the operator's answer rides on its own ask step");
    }

    [Fact]
    public async Task Only_an_answered_ask_human_contributes()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.Plan, "{}", AnswerOutcome("ignored"));   // wrong verb — an answer on a plan outcome is not an ask
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.AskHuman, "{}", "{}");                   // an ask still awaiting the human — no answer yet

        (await new AskAnswerFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None))
            .ShouldBeEmpty("only an ANSWERED ask_human contributes — a non-ask verb or a still-pending ask adds nothing");
    }

    [Fact]
    public async Task A_pending_gate_escalation_ask_is_flagged_the_moment_the_run_parks()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.AskHuman, EscalationPayload(), "{}");   // parked, unanswered

        var facts = await new AskAnswerFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var key = SupervisorDecisionTimelineMap.EventId(log.Rows.Single());
        facts[key].ReviewEscalation.ShouldBeTrue("the review-blocked framing shows while the ask is still pending, not only after the answer lands");
        facts[key].Answer.ShouldBeNull("no answer yet");
    }

    [Fact]
    public async Task An_answered_escalation_carries_both_the_answer_and_the_flag()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.AskHuman, EscalationPayload(), AnswerOutcome("approve"));

        var facts = await new AskAnswerFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var key = SupervisorDecisionTimelineMap.EventId(log.Rows.Single());
        facts[key].Answer.ShouldBe("approve");
        facts[key].ReviewEscalation.ShouldBeTrue();
    }

    [Fact]
    public async Task An_ordinary_answered_ask_is_not_flagged()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.AskHuman, "{}", AnswerOutcome("go ahead"));

        var facts = await new AskAnswerFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        facts[SupervisorDecisionTimelineMap.EventId(log.Rows.Single())].ReviewEscalation
            .ShouldBeFalse("a content ask / a confirmation card never reads as a review escalation — the marker is payload-pinned");
    }

    [Fact]
    public async Task A_pending_plan_confirmation_ask_is_flagged_so_the_generic_answer_bar_stays_off()
    {
        var runId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var log = new FakeSupervisorDecisionLog();

        // A REAL confirmation card's payload — built by the production gate so the marker can't drift.
        log.SeedTerminal(runId, teamId, SupervisorDecisionKinds.AskHuman, Core.Services.Supervisor.SupervisorPlanConfirmation.IntoAskHuman(planVersion: 1, itemCount: 2).PayloadJson, "{}");

        var facts = await new AskAnswerFactsSource(log).GatherAsync(runId, teamId, CancellationToken.None);

        var key = SupervisorDecisionTimelineMap.EventId(log.Rows.Single());
        facts[key].PlanConfirmation.ShouldBeTrue("the plan checklist card is that park's answer surface — the frontend suppresses the generic bar");
        facts[key].ReviewEscalation.ShouldBeFalse("the two gates never claim each other's cards");
    }

    /// <summary>A REAL escalation card's payload — built by the production gate so the marker can't drift from the recognizer.</summary>
    private static string EscalationPayload() =>
        Core.Services.Supervisor.SupervisorGateEscalation.IntoAskHuman(
            new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = "{}" },
            new Messages.Review.CriticVerdict { Mode = Messages.Enums.ReviewMode.Gate, Approved = false, Rationale = "blocked" }).PayloadJson;

    private static string AnswerOutcome(string answer) => System.Text.Json.JsonSerializer.Serialize(new { answer });
}

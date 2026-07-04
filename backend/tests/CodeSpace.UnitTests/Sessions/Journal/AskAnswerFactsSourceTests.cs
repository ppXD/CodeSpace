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

    private static string AnswerOutcome(string answer) => System.Text.Json.JsonSerializer.Serialize(new { answer });
}

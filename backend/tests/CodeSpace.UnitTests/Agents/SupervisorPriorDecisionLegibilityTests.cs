using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit (D6): the prior-decision LINE the supervisor brain reads for every verb that had no dedicated renderer.
/// Before this, an answered <c>ask_human</c> reached the model as raw jsonb — question, answer AND the internal
/// <c>askHumanToken</c> — and the live plan decision dumped its whole payload as json. Both are the decisions the
/// next turn is supposed to act on, so both are rendered legibly here and pinned against re-regression.
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorPriorDecisionLegibilityTests
{
    private const string Token = "sup#turn1#ask-1a2b3c4d";

    private static SupervisorTurnContext Context(params SupervisorPriorDecision[] prior) =>
        new() { Goal = "ship the feature", TurnNumber = prior.Length, PriorDecisions = prior };

    private static SupervisorPriorDecision Ask(long sequence, string payloadJson, string? outcomeJson) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = SupervisorDecisionKinds.AskHuman,
        Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
    };

    // ── ask_human ───────────────────────────────────────────────────────────────────

    [Fact]
    public void An_answered_ask_human_renders_the_question_and_the_answer_never_the_wait_token()
    {
        var prior = Ask(1, """{"question":"Which database should the migration target?"}""",
            SupervisorOutcome.FoldAnswer("Which database should the migration target?", Token, "Use the staging Postgres, not prod."));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(prior));

        prompt.ShouldContain("Which database should the migration target?", customMessage: "the brain must see the question it asked");
        prompt.ShouldContain("Use the staging Postgres, not prod.", customMessage: "and the human's actual answer");
        prompt.ShouldNotContain(Token, customMessage: "the internal wait token is server plumbing — it must never reach the model");
        prompt.ShouldNotContain("askHumanToken", customMessage: "no raw outcome jsonb for an answered ask_human");
    }

    [Fact]
    public void A_parked_unanswered_ask_human_says_so_and_still_hides_the_token()
    {
        var prior = Ask(1, """{"question":"Should I open a PR?"}""", SupervisorOutcome.FoldAnswer("Should I open a PR?", Token, answer: null));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(prior));

        prompt.ShouldContain("Should I open a PR?");
        prompt.ShouldContain("has NOT answered", customMessage: "a pending question must read as pending, not as answered");
        prompt.ShouldNotContain(Token);
    }

    [Fact]
    public void A_degraded_ask_human_with_no_human_surface_says_the_question_was_never_delivered()
    {
        var prior = Ask(1, """{"question":"Which branch?"}""", """{"askHuman":"unsupported","reason":"no conversation is bound to this run"}""");

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(prior));

        prompt.ShouldContain("Which branch?");
        prompt.ShouldContain("never delivered", customMessage: "a degraded ask must not read as merely pending — no human will ever answer it");
    }

    [Fact]
    public void An_ask_human_row_carrying_no_question_answer_or_token_keeps_the_raw_line()
    {
        // The compaction tape writes generic ask_human rows with no question at all. There is nothing legible to
        // render for those, so the raw line stays — byte-identical, and the digest still sees the payload.
        var prior = Ask(1, """{"note":"marker-1-seq"}""", "{}");

        LlmSupervisorDecider.BuildUserPromptForTest(Context(prior))
            .ShouldContain("marker-1-seq", customMessage: "a shapeless ask_human row must still render its payload");
    }

    [Fact]
    public void An_approved_amend_card_renders_the_amendment_and_its_co_sign_state()
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "the check invokes npm, which this repository does not have",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } },
        });
        var prior = Ask(1, card.PayloadJson, """{"question":"q","answer":"approve"}""");

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(prior));

        prompt.ShouldContain("your acceptance amendment for subtask 's1'", Case.Sensitive,
            "the brain must read this card as ITS amendment proposal for that subtask, not as a generic question");
        prompt.ShouldContain("APPROVED: the co-signed check is now the one in force", Case.Sensitive, "and its co-sign state");
    }

    [Fact]
    public void A_pending_amend_card_reads_as_awaiting_the_co_sign()
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s2", Reason = "impossible assertion", Waive = true,
        });
        var prior = Ask(1, card.PayloadJson, SupervisorOutcome.FoldAnswer("q", Token, answer: null));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(prior));

        prompt.ShouldContain("'s2'");
        prompt.ShouldContain("waive", Case.Insensitive);
        prompt.ShouldContain("not co-signed", customMessage: "an unapproved amendment changes no oracle — the brain must not act as if it had");
        prompt.ShouldNotContain(Token);
    }

    // ── plan ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void The_live_plan_renders_one_line_per_item_with_its_state_not_raw_payload_json()
    {
        var plan = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"goal":"ship","subtasks":[{"id":"s1","title":"Add the parser","instruction":"write the parser"},{"id":"s2","title":"Wire it up","instruction":"call the parser","dependsOn":["s1"]}]}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(plan));

        prompt.ShouldContain("[s1] Add the parser: pending", customMessage: "each plan item renders id, title and its LIVE state");
        prompt.ShouldContain("[s2] Wire it up: pending");
        prompt.ShouldContain("depends on s1", customMessage: "the authored DAG edge stays legible");
        prompt.ShouldContain("write the parser", customMessage: "the instruction is the text a revisedInstruction is authored against — it must survive");
        prompt.ShouldNotContain("""- plan: payload={""", customMessage: "the live plan no longer dumps raw json");
    }

    // ── a budget-blocked wave ───────────────────────────────────────────────────────

    [Fact]
    public void A_budget_blocked_wave_renders_legibly_instead_of_raw_jsonb()
    {
        var spawn = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Spawn, Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = """{"subtaskIds":["s1","s2"]}""",
            OutcomeJson = """{"budgetBlocked":["s1","s2"],"reason":"cap reached","committedUsd":9.50,"capUsd":10.00}""",
        };

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(spawn));

        prompt.ShouldContain("BLOCKED by the run's budget", customMessage: "a budget-blocked wave must read as a refusal, not as raw json");
        prompt.ShouldContain("cap reached");
        prompt.ShouldContain("s1, s2", customMessage: "naming which units were withheld");
        prompt.ShouldContain("staged NOTHING", customMessage: "and that re-authoring the same wave is futile");
        prompt.ShouldNotContain("budgetBlocked", customMessage: "no raw outcome key reaches the model");
    }
}

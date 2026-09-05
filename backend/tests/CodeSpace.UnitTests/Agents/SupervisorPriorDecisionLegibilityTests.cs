using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Executors;
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
        // The bytes come from the PRODUCTION writer: the hand-typed {"askHuman":"unsupported","reason":…} this
        // fixture used to carry is a shape no code path ever records, so it pinned the fallback arm rather than the
        // degraded one it names.
        var prior = Ask(1, """{"question":"Which branch?"}""", RealSupervisorActionExecutor.NoSurfaceAskHumanOutcomeJson("Which branch?"));

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

    [Fact]
    public void A_precondition_REFUSED_amend_card_names_the_server_reason_and_never_reads_as_no_surface()
    {
        // The bug this pins: a refused card records {askHuman:"rejected", reason} — no token, no answer — so it fell
        // into the DEGRADED arm and told the brain "no human surface was bound to this run", which is a different
        // fact with a different next move, while dropping the one thing the turn produced: WHY it was refused. The
        // raw jsonb line it replaced at least showed the reason.
        const string reason = "subtask 's1's check RAN and rejected the work (exit 1) — that is evidence against the WORK, not the check";

        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "the assertion is impossible",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } },
        });
        var prior = Ask(1, card.PayloadJson, RealSupervisorActionExecutor.RejectedAskHumanOutcomeJson(reason));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(prior));

        prompt.ShouldContain("REFUSED by the server", customMessage: "a refused ask must read as a refusal");
        prompt.ShouldContain("evidence against the WORK", customMessage: "the server's named reason is the whole content of the turn — dropping it is the regression");
        prompt.ShouldContain("refused again", customMessage: "and re-proposing the same amendment is futile");
        prompt.ShouldNotContain("no human surface was bound", customMessage: "a refusal is NOT a missing surface — they imply opposite next moves");
        prompt.ShouldNotContain("has NOT answered", customMessage: "nothing was posted, so nothing is pending");
        prompt.ShouldNotContain("askHuman", customMessage: "no raw outcome key reaches the model");
    }

    [Fact]
    public void A_refused_ordinary_question_names_the_reason_without_the_amendment_wording()
    {
        var prior = Ask(1, """{"question":"Which branch?"}""", RealSupervisorActionExecutor.RejectedAskHumanOutcomeJson("the ask_human decision carried no question text"));

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(prior));

        prompt.ShouldContain("REFUSED by the server");
        prompt.ShouldContain("carried no question text");
        prompt.ShouldNotContain("acceptance check is still the one in force", customMessage: "a plain question refusal says nothing about any subtask's oracle");
    }

    // ── plan ────────────────────────────────────────────────────────────────────────

    private static SupervisorPriorDecision Plan(string payloadJson) => new()
    {
        Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Plan,
        Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson,
    };


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

    [Fact]
    public void The_live_plan_shows_the_EFFECTIVE_acceptance_check_not_the_amended_away_original()
    {
        // The plan arm printed the AUTHORED check straight off the payload, bypassing the co-sign overlay the
        // recitation applies two blocks later. A co-signed replacement therefore reached the brain as its own dead
        // original — the very bytes a human already ruled on — inviting a second amendment of a check nothing grades.
        var plan = Plan("""{"goal":"ship","subtasks":[{"id":"s1","title":"Add the parser","instruction":"write the parser","acceptance":{"command":["npm","test"]}}]}""");

        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload
        {
            SubtaskId = "s1", Reason = "npm is not installed in this repository",
            Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "dotnet", "test" } },
        });
        var approved = Ask(2, card.PayloadJson, """{"question":"q","answer":"approve"}""");

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(plan, approved));

        prompt.ShouldContain("AMENDED by a co-signed amendment", customMessage: "the co-signed replacement is the check in force and must be marked as such");
        prompt.ShouldContain("dotnet test", customMessage: "the effective command is what the brain writes its next move against");
        prompt.ShouldNotContain("npm test", customMessage: "the amended-away original must not be shown as live");
    }

    [Fact]
    public void A_waived_plan_item_says_WAIVED_and_never_recites_a_check_nothing_grades()
    {
        var plan = Plan("""{"goal":"ship","subtasks":[{"id":"s1","title":"Add the parser","instruction":"write the parser","acceptance":{"command":["npm","test"]}}]}""");

        var card = SupervisorAmendAcceptance.IntoAskHuman(new SupervisorAmendAcceptancePayload { SubtaskId = "s1", Reason = "unverifiable by design", Waive = true });
        var approved = Ask(2, card.PayloadJson, """{"question":"q","answer":"approve"}""");

        var prompt = LlmSupervisorDecider.BuildUserPromptForTest(Context(plan, approved));

        prompt.ShouldContain("WAIVED by a co-signed amendment");
        prompt.ShouldContain("WAIVED is not PASSED", customMessage: "the B2 invariant holds at every door, this one included");
        prompt.ShouldNotContain("npm test", customMessage: "a waived item carries NO oracle — reciting one invents a contract the run does not have");
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

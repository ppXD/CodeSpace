using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E4 ask_human MID-LOOP human checkpoint — the PURE pieces, pinned without a DB. The
/// executor's real card-post + Action-wait staging + the node suspend are pinned over real Postgres + the real
/// engine at the integration tier (<c>SupervisorAskHumanFlowTests</c>); here we pin:
/// <list type="bullet">
///   <item>the <c>{question}</c> payload round-trips through the projector (the canonical shape the ledger hashes);</item>
///   <item>the THREE-way suspend classification — an ask_human outcome (carrying its wait token) replays to
///         PARK-ON-HUMAN, distinct from the agent barrier (park-on-agents) and the synchronous self-advance;</item>
///   <item>the answer-FOLD: a recorded human answer is stamped into the ask_human outcome the next turn reads,
///         preserving the question + token (so the decider sees "you asked X, the human answered Y").</item>
/// </list>
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorAskHumanTests
{
    // ── The {question} payload round-trips through the projector ─────────────────────

    [Fact]
    public void AskHuman_projects_the_question()
    {
        var decision = SupervisorDecisionProjector.Project(new SupervisorModelDecision
        {
            Kind = SupervisorDecisionKinds.AskHuman,
            AskHuman = new SupervisorAskHumanPayload { Question = "which approach: rewrite or patch?" },
        });

        decision.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman);
        decision.IsTerminal.ShouldBeFalse("ask_human is not terminal — the loop re-enters after the human answers");

        var payload = JsonSerializer.Deserialize<SupervisorAskHumanPayload>(decision.PayloadJson, AgentJson.Options);
        payload!.Question.ShouldBe("which approach: rewrite or patch?", "the {question} round-trips through the canonical payload");
    }

    [Fact]
    public void AskHuman_projection_is_deterministic()
    {
        var model = new SupervisorModelDecision { Kind = SupervisorDecisionKinds.AskHuman, AskHuman = new SupervisorAskHumanPayload { Question = "?" } };

        SupervisorDecisionProjector.Project(model).PayloadJson
            .ShouldBe(SupervisorDecisionProjector.Project(model).PayloadJson, "same model decision → byte-identical canonical payload (the idempotency-key stability the ledger relies on)");
    }

    // ── The three-way suspend classification ─────────────────────────────────────────

    [Fact]
    public void An_ask_human_execution_classifies_as_park_on_human_not_agent_barrier_not_self_advance()
    {
        var execution = SupervisorExecution.ParkedOnHuman("""{"question":"q","askHumanToken":"tok-1","answer":null}""", "tok-1");

        execution.HumanWaitToken.ShouldBe("tok-1", "the human-park carries the Action wait token");
        execution.ParkedAgentWaitCount.ShouldBe(0, "ask_human is NOT the agent barrier — no staged agent waits");

        // Distinct from the two other paths.
        SupervisorExecution.Synchronous("{}").HumanWaitToken.ShouldBeNull("a synchronous self-advance is not a human park");
        SupervisorExecution.ParkedOnAgents("{}", 2).HumanWaitToken.ShouldBeNull("the agent barrier is not a human park");
    }

    [Fact]
    public void An_ask_human_turn_result_parks_on_the_human_not_finished_not_agents()
    {
        var next = new SupervisorTurnContext { NodeId = "sup", TurnNumber = 1 };
        var result = SupervisorTurnResult.ParkOnHuman(SupervisorDecisionKinds.AskHuman, next, "tok-1");

        result.IsFinished.ShouldBeFalse("an ask_human turn parks — it does not finish the loop");
        result.ParkedOnHuman.ShouldBeTrue();
        result.ParkedOnAgentWaits.ShouldBeFalse("a human park is NOT the agent barrier");
        result.HumanWaitToken.ShouldBe("tok-1");
        result.NextTurn!.TurnNumber.ShouldBe(1, "the park carries the next turn's number");
    }

    [Fact]
    public void A_replayed_ask_human_outcome_re_derives_the_human_park_without_re_posting()
    {
        // The turn service's replay path (ReplayExecution) re-derives the suspend classification from the
        // recorded outcome alone — an ask_human outcome carries its token → re-park on the EXISTING wait, no
        // re-post. This pins the data the replay reads (the executor + replay both read the token from here).
        var outcome = SupervisorOutcome.FoldAnswer("q", "tok-9", answer: null);

        SupervisorOutcome.ReadHumanWaitToken(outcome).ShouldBe("tok-9", "a replay re-derives the SAME wait token → re-park, never re-post");
        SupervisorOutcome.ReadStagedAgentCount(outcome).ShouldBe(0, "an ask_human outcome has no agent count — it is not the agent barrier");
    }

    // ── SupervisorOutcome helpers (the canonical ask_human shape) ────────────────────

    [Fact]
    public void Folding_the_answer_preserves_the_review_chain_and_usage_on_the_parked_row()
    {
        // The escalation ask's row carries the WHOLE Gate ladder (its verdicts + the draft attribution) + the
        // authoring usage. The answer fold must set ONLY the answer — the old bare re-emit erased the exchange
        // off the durable tape the moment the human answered (the reviewer-proven J defect).
        var reviews = new[]
        {
            new SupervisorDecisionReview { Approved = false, Rationale = "thin", Issues = new[] { "no tests (evidence: none)" }, Scope = "decision", DraftAttribution = "spawn draft · authored via m9 · 8,000 tokens", ViaAgent = true },
            new SupervisorDecisionReview { Approved = false, Rationale = "still thin", Scope = "decision" },
        };
        var parked = SupervisorOutcome.WriteReviews(
            SupervisorOutcome.WriteModelUsage(SupervisorOutcome.FoldAnswer("Proceed?", "tok-1", answer: null), new SupervisorModelUsage { Model = "m9", InputTokens = 7000, OutputTokens = 1000 }),
            reviews);

        var folded = SupervisorOutcome.FoldAnswerOnto(parked, "approve");

        SupervisorOutcome.ReadAskHumanAnswer(folded).ShouldBe("approve");
        SupervisorOutcome.ReadAskHumanQuestion(folded).ShouldBe("Proceed?", "the question survives the fold");
        SupervisorOutcome.ReadHumanWaitToken(folded).ShouldBe("tok-1", "the token survives — a replay still re-derives the same park");
        SupervisorOutcome.ReadModelUsage(folded).ShouldNotBeNull("the authoring usage survives");

        var read = SupervisorOutcome.ReadReviews(folded);
        read.Count.ShouldBe(2, "the escalation's whole ladder survives the answer");
        read[0].DraftAttribution.ShouldBe("spawn draft · authored via m9 · 8,000 tokens", "the draft attribution — the journal's replaced-a-draft line — survives");
        read[0].ViaAgent.ShouldBeTrue();

        SupervisorOutcome.FoldAnswerOnto(folded, "approve").ShouldBe(folded, "re-folding the same answer is byte-identical — the rehydrate fold stays idempotent");
    }

    [Fact]
    public void The_human_wait_key_is_per_turn_and_ask_suffixed()
    {
        SupervisorOutcome.HumanWaitKey("sup", 0).ShouldBe("sup#turn0#ask");
        SupervisorOutcome.HumanWaitKey("sup", 1).ShouldBe("sup#turn1#ask");
        SupervisorOutcome.HumanWaitKey("sup", 0).ShouldNotBe(SupervisorOutcome.HumanWaitKey("sup", 1), "distinct per turn → a later ask never collides with this one");
    }

    [Fact]
    public void A_non_ask_outcome_has_no_human_wait_token()
    {
        SupervisorOutcome.ReadHumanWaitToken("""{"planned":["a"],"count":1}""").ShouldBeNull();
        SupervisorOutcome.ReadHumanWaitToken("""{"agentRunIds":["x"],"agentCount":1}""").ShouldBeNull();
        SupervisorOutcome.ReadHumanWaitToken(null).ShouldBeNull();
        SupervisorOutcome.ReadHumanWaitToken("not json").ShouldBeNull("a malformed outcome reads no token, never crashes");
    }

    // ── RejectedAskHumanOutcome: P0-2 action schema validation ─────────────────────────

    [Fact]
    public void The_rejected_ask_human_outcome_names_the_specific_defect()
    {
        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.RejectedAskHumanOutcome, AgentJson.Options);
        using var doc = JsonDocument.Parse(json);

        doc.RootElement.GetProperty("askHuman").GetString().ShouldBe("rejected");
        doc.RootElement.GetProperty("reason").GetString().ShouldBe("the ask_human decision carried no question text");
    }

    [Fact]
    public void A_rejected_ask_human_outcome_carries_no_wait_token_so_it_reads_as_a_synchronous_self_advance()
    {
        var json = JsonSerializer.Serialize(RealSupervisorActionExecutor.RejectedAskHumanOutcome, AgentJson.Options);

        SupervisorOutcome.ReadHumanWaitToken(json).ShouldBeNull("the rejection never posts a card or stages a wait — no human interaction is spent on it");
    }

    // ── The answer-fold: a recorded human answer surfaces in the next turn's context ──

    [Fact]
    public void Folding_an_answer_stamps_it_while_preserving_the_question_and_token()
    {
        // Before the human answers: the executor records the outcome with answer == null.
        var beforeAnswer = SupervisorOutcome.FoldAnswer("which approach?", "tok-7", answer: null);
        SupervisorOutcome.ReadAskHumanAnswer(beforeAnswer).ShouldBeNull("no answer yet → null, so the decider knows it's still waiting");

        // After the human answers: the rehydrate folds the resolved-wait comment into the SAME shape.
        var afterAnswer = SupervisorOutcome.FoldAnswer(
            SupervisorOutcome.ReadAskHumanQuestion(beforeAnswer),
            SupervisorOutcome.ReadHumanWaitToken(beforeAnswer)!,
            answer: "patch it");

        SupervisorOutcome.ReadAskHumanAnswer(afterAnswer).ShouldBe("patch it", "the human's answer is folded in for the next turn");
        SupervisorOutcome.ReadAskHumanQuestion(afterAnswer).ShouldBe("which approach?", "the question is preserved across the fold");
        SupervisorOutcome.ReadHumanWaitToken(afterAnswer).ShouldBe("tok-7", "the token is preserved so a replay still re-derives the park");
    }

    [Fact]
    public void The_fold_is_idempotent()
    {
        var folded = SupervisorOutcome.FoldAnswer("q", "tok-1", "the answer");

        var refolded = SupervisorOutcome.FoldAnswer(
            SupervisorOutcome.ReadAskHumanQuestion(folded),
            SupervisorOutcome.ReadHumanWaitToken(folded)!,
            SupervisorOutcome.ReadAskHumanAnswer(folded));

        refolded.ShouldBe(folded, "re-folding an already-folded outcome is a no-op (re-reading on every rehydrate is safe)");
    }
}

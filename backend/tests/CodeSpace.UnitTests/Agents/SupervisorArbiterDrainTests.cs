using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.UnitTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the supervisor's decision-ARBITER drain (<see cref="SupervisorTurnService.ArbitratePendingChildDecisionsAsync"/>,
/// D4c-2), driven against the honest fakes at the queue / brain / answer seams. Pins: an ANSWER verdict actuates the
/// supervisor-author answer path verbatim; an ESCALATE (or a floor-forced RequiresHuman) is LEFT in the cross-grain queue
/// (the answer path is hit but the decision is not resolved — never thrown); EVERY pending child is arbitrated this turn;
/// the brain gets the run's team / brain-model / goal; cancellation propagates; and the drain runs BEFORE the delivery
/// decider (and always falls through to it).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorArbiterDrainTests
{
    private static readonly Guid TeamId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid BrainModelId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public async Task No_pending_children_calls_neither_the_arbiter_nor_the_answer_path()
    {
        var arbiter = new FakeDecisionArbiter();
        var answer = new FakeDecisionAnswerService();

        await Drain(arbiter, answer).ArbitratePendingChildDecisionsAsync(Context(), CancellationToken.None);

        arbiter.JudgedDecisionIds.ShouldBeEmpty("no blocked children → the brain is never consulted (the common path is DB-free + LLM-free)");
        answer.Calls.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_answer_verdict_is_actuated_via_the_supervisor_author_path_verbatim()
    {
        var decision = Pending();
        var arbiter = new FakeDecisionArbiter(_ => ArbiterVerdict.Answer(new[] { "a" }, null, "low-risk + recommended"));
        var answer = new FakeDecisionAnswerService(DecisionAnswerOutcome.Answered);

        await Drain(arbiter, answer).ArbitratePendingChildDecisionsAsync(Context(decision), CancellationToken.None);

        var call = answer.Calls.ShouldHaveSingleItem();
        call.DecisionId.ShouldBe(decision.Id);
        call.SelectedOptions.ShouldBe(new[] { "a" });
        call.FreeText.ShouldBeNull();
        call.Rationale.ShouldBe("low-risk + recommended", "the arbiter's rationale is recorded on the answer (AC3)");
        call.TeamId.ShouldBe(TeamId, "the run's real team — never model-supplied");
    }

    [Fact]
    public async Task A_free_text_answer_maps_through_to_the_answer_path()
    {
        var arbiter = new FakeDecisionArbiter(_ => ArbiterVerdict.Answer(Array.Empty<string>(), "rename to user_id", "obvious"));
        var answer = new FakeDecisionAnswerService();

        await Drain(arbiter, answer).ArbitratePendingChildDecisionsAsync(Context(Pending()), CancellationToken.None);

        var call = answer.Calls.ShouldHaveSingleItem();
        call.FreeText.ShouldBe("rename to user_id");
        call.SelectedOptions.ShouldBeEmpty();
    }

    [Fact]
    public async Task An_escalate_verdict_leaves_the_decision_in_the_queue_and_never_answers()
    {
        var arbiter = new FakeDecisionArbiter(_ => ArbiterVerdict.Escalate("too risky to decide"));
        var answer = new FakeDecisionAnswerService();

        await Drain(arbiter, answer).ArbitratePendingChildDecisionsAsync(Context(Pending()), CancellationToken.None);

        arbiter.JudgedDecisionIds.Count.ShouldBe(1, "the brain still judged it");
        answer.Calls.ShouldBeEmpty("an escalate is LEFT for a human in the cross-grain queue — the supervisor never auto-answers it");
    }

    [Fact]
    public async Task A_floor_forced_RequiresHuman_leaves_it_for_a_human_without_throwing()
    {
        // The arbiter said answer, but the answer path's floor re-check overrode it → RequiresHuman. The drain must treat
        // that as "left for a human" (defense-in-depth — the floor has the last word), NOT a failure to retry or throw.
        var arbiter = new FakeDecisionArbiter(_ => ArbiterVerdict.Answer(new[] { "a" }, null, "the arbiter was wrong"));
        var answer = new FakeDecisionAnswerService(DecisionAnswerOutcome.RequiresHuman);

        await Should.NotThrowAsync(() => Drain(arbiter, answer).ArbitratePendingChildDecisionsAsync(Context(Pending()), CancellationToken.None));

        answer.Calls.Count.ShouldBe(1, "it tried to answer; the floor refused — the decision stays parked for a human");
    }

    [Fact]
    public async Task Every_pending_child_is_arbitrated_in_one_turn()
    {
        var a = Pending();
        var b = Pending();
        var c = Pending();
        var arbiter = new FakeDecisionArbiter(d => d.Id == b.Id ? ArbiterVerdict.Escalate("hard") : ArbiterVerdict.Answer(new[] { "a" }, null, "ok"));
        var answer = new FakeDecisionAnswerService();

        await Drain(arbiter, answer).ArbitratePendingChildDecisionsAsync(Context(a, b, c), CancellationToken.None);

        arbiter.JudgedDecisionIds.ShouldBe(new[] { a.Id, b.Id, c.Id });   // all three are judged this turn, in order
        answer.Calls.Select(x => x.DecisionId).ShouldBe(new[] { a.Id, c.Id }, "the two answerable ones are answered; the escalated one is left in the queue");
    }

    [Fact]
    public async Task The_arbiter_receives_the_runs_team_brain_model_and_goal()
    {
        var decision = Pending();
        var arbiter = new FakeDecisionArbiter();

        await Drain(arbiter, new FakeDecisionAnswerService()).ArbitratePendingChildDecisionsAsync(Context(decision), CancellationToken.None);

        arbiter.JudgedDecisionIds.ShouldBe(new[] { decision.Id });
        arbiter.LastTeamId.ShouldBe(TeamId);
        arbiter.LastSupervisorModelId.ShouldBe(BrainModelId, "the brain runs on the operator's supervisor model — the same row the decider uses");
        arbiter.LastGoal.ShouldBe("ship it");
    }

    [Fact]
    public async Task One_childs_answer_path_failure_does_not_abort_arbitrating_the_others()
    {
        // Best-effort per child: an infra throw answering the 2nd child must not stop the 1st + 3rd being arbitrated.
        var a = Pending();
        var b = Pending();
        var c = Pending();
        var arbiter = new FakeDecisionArbiter(_ => ArbiterVerdict.Answer(new[] { "a" }, null, "ok"));
        var answer = new FakeDecisionAnswerService(DecisionAnswerOutcome.Answered, throwOnCall: 2);

        await Should.NotThrowAsync(() => Drain(arbiter, answer).ArbitratePendingChildDecisionsAsync(Context(a, b, c), CancellationToken.None));

        arbiter.JudgedDecisionIds.ShouldBe(new[] { a.Id, b.Id, c.Id });
        answer.Calls.Select(x => x.DecisionId).ShouldBe(new[] { a.Id, c.Id }, "the failed child (b) is skipped + left in the queue; a and c are answered — one bad child never aborts the drain");
    }

    [Theory]
    [InlineData(DecisionAnswerOutcome.AlreadyResolved)]
    [InlineData(DecisionAnswerOutcome.NotFound)]
    [InlineData(DecisionAnswerOutcome.Invalid)]
    public async Task A_benign_or_invalid_answer_outcome_is_left_and_the_drain_continues(DecisionAnswerOutcome outcome)
    {
        // AlreadyResolved/NotFound (a human/deadline raced it) + Invalid (a mis-shaped arbiter answer) are NOT failures —
        // the decision is left for a human, never thrown, and the drain still arbitrates the following child.
        var a = Pending();
        var b = Pending();
        var arbiter = new FakeDecisionArbiter(_ => ArbiterVerdict.Answer(new[] { "a" }, null, "ok"));
        var answer = new FakeDecisionAnswerService(outcome);

        await Should.NotThrowAsync(() => Drain(arbiter, answer).ArbitratePendingChildDecisionsAsync(Context(a, b), CancellationToken.None));

        answer.Calls.Count.ShouldBe(2, "both children are attempted; a benign/invalid outcome never throws and never short-circuits the loop");
    }

    [Fact]
    public async Task A_no_spawn_rehydrate_skips_the_queue_read_entirely()
    {
        // The DB-gate proof at the source: a tape with no spawn/retry/resolve never derives a child-id set, so the queue
        // read is never issued (LastAgentRunIds stays null) — the no-spawn turn is byte-identical to pre-D4c-2.
        var runId = Guid.NewGuid();
        var queue = new FakeDecisionQueue();
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(runId, TeamId, SupervisorDecisionKinds.Plan, """{"subtasks":["a"]}""", """{"planned":["a"]}""");
        var service = new SupervisorTurnService(ledger, new StubSupervisorDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), queue, new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, new FakePublishManifestStore(), new FakeSupervisorPublishedBranchResolver(), NullLogger<SupervisorTurnService>.Instance);

        var context = await service.RehydrateFromDecisionLogAsync(runId, TeamId, "sup", "goal", goalConfig: null, CancellationToken.None);

        context.PendingChildDecisions.ShouldBeEmpty();
        queue.LastAgentRunIds.ShouldBeNull("a no-spawn tape never calls the queue read — the gate short-circuits before it");
    }

    [Fact]
    public async Task Cancellation_propagates_it_is_not_swallowed()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Drain(new FakeDecisionArbiter(), new FakeDecisionAnswerService()).ArbitratePendingChildDecisionsAsync(Context(Pending()), cts.Token));
    }

    [Fact]
    public async Task The_drain_runs_before_the_delivery_decider_and_falls_through_to_it()
    {
        // ChooseDecisionAsync places the drain AFTER the pre-bound guard and BEFORE the decider: a fresh turn-0 context
        // auto-answers its blocked child, THEN the decider chooses the turn's actual decision (here a plan).
        var arbiter = new FakeDecisionArbiter(_ => ArbiterVerdict.Answer(new[] { "a" }, null, "ok"));
        var answer = new FakeDecisionAnswerService(DecisionAnswerOutcome.Answered);

        var decision = await Drain(arbiter, answer).ChooseDecisionAsync(Context(Pending()), SupervisorGoalPlan.From(null), depth: 0, CancellationToken.None);

        answer.Calls.Count.ShouldBe(1, "the drain ran (auto-answered the child) before the decider");
        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan, "the turn still falls through to the delivery decider's decision");
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static SupervisorTurnService Drain(FakeDecisionArbiter arbiter, FakeDecisionAnswerService answer) =>
        new(new FakeSupervisorDecisionLog(), new StubSupervisorDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), arbiter, answer, new FakeWorkPlanStore(), null!, null!, new FakePublishManifestStore(), new FakeSupervisorPublishedBranchResolver(), NullLogger<SupervisorTurnService>.Instance);

    private static SupervisorTurnContext Context(params PendingDecision[] pending) => new()
    {
        Goal = "ship it",
        TeamId = TeamId,
        SupervisorModelId = BrainModelId,
        PendingChildDecisions = pending,
    };

    private static PendingDecision Pending() => new()
    {
        Id = Guid.NewGuid(),
        Grain = DecisionResumeBackends.ToolLedger,
        RootTraceId = Guid.NewGuid(),
        AgentRunId = Guid.NewGuid(),
        DecisionType = DecisionTypes.ChooseOne,
        Question = "which migration path?",
        Options = new[] { new DecisionOption { Id = "a", Label = "Path A" } },
        RiskLevel = "low",
        Policy = DecisionPolicies.SupervisorFirst,
        CreatedAt = DateTimeOffset.UnixEpoch,
    };
}

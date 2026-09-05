using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows.Supervisor;

/// <summary>
/// Always-on (no model, no Postgres) teeth for the multi-turn trajectory harness + scorer (A3): drive SCRIPTED
/// deciders through the simulated happy-path environment and prove the scorer (a) PASSES a brain that drives to
/// completion (plan→spawn→merge→stop) and (b) FAILS a brain that loops forever or quits empty. This is the harness
/// the real-model trajectory gate scores against — pinning it here means the live gate measures the BRAIN, not a
/// broken scorer.
/// </summary>
[Trait("Category", "Integration")]
public sealed class SupervisorTrajectoryEvalTests
{
    [Fact]
    public async Task A_brain_that_drives_plan_spawn_merge_stop_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new ConvergingDecider(), maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue(note);
    }

    /// <summary>
    /// A non-terminating verdict must say what the brain looped ON, not just that it looped. This lane emits none of
    /// the supervisor's own log stream, so the verb sequence was the ONLY thing a reader got — which is why the eval
    /// sat red on main for days across a dozen unrelated commits: "plan→spawn×7" names a symptom nobody can act on.
    /// A loop of REJECTED decisions and a loop of real fan-outs are completely different bugs and rendered identically.
    /// </summary>
    [Fact]
    public void A_looping_verdict_names_what_each_staging_verb_actually_did()
    {
        const string rejected = """{"agentRunIds":[],"agentCount":0,"spawn":"rejected","reason":"the spawn named subtask id(s) [ghost] the current plan never declared"}""";
        const string withheld = """{"agentRunIds":[],"agentCount":0,"blockedSubtasks":[{"subtaskId":"dependent","reason":"neither a branch nor a patch was captured"}]}""";
        const string staged = """{"agentRunIds":["1e4b8f4c-0000-0000-0000-000000000001"],"agentCount":1}""";
        const string acceptedButEmpty = """{"agentRunIds":[],"agentCount":0,"note":"no subtasks to spawn"}""";

        var (ok, note) = SupervisorTrajectoryScore.Score(new SupervisorTrajectoryResult
        {
            Kinds = new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Spawn },
            ReachedStop = false,
            HitTurnCap = true,
            Ledger = new[]
            {
                LedgerEntry(SupervisorDecisionKinds.Plan, 1, "{}"),
                LedgerEntry(SupervisorDecisionKinds.Spawn, 2, rejected),
                LedgerEntry(SupervisorDecisionKinds.Spawn, 3, withheld),
                LedgerEntry(SupervisorDecisionKinds.Spawn, 4, staged),
                LedgerEntry(SupervisorDecisionKinds.Spawn, 5, acceptedButEmpty),
                LedgerEntry(SupervisorDecisionKinds.Spawn, 6, null),
            },
        });

        ok.ShouldBeFalse("a run that never stopped is still a failure — this change is about the NOTE, not the verdict");

        note.ShouldContain("plan→spawn→spawn→spawn→spawn", Case.Insensitive, "the verb sequence is still there — this is additive");
        note.ShouldContain("REJECTED", Case.Insensitive, "a refused decision must be distinguishable from an accepted one");
        note.ShouldContain("never declared", Case.Insensitive, "the server's own reason reaches the reader verbatim");
        note.ShouldContain("WITHHELD", Case.Insensitive, "a blocked hand-off is a different bug from a refusal and must read as one");
        note.ShouldContain("dependent", Case.Insensitive, "the withheld unit is named");
        note.ShouldContain("staged 1 agent", Case.Insensitive, "a verb that genuinely fanned out says so — otherwise a real loop and a rejected loop still look alike");
        note.ShouldContain("staged NOTHING", Case.Insensitive, "accepted-but-empty is its own third case, not silently folded into either other one");
        note.ShouldContain("did not complete", Case.Insensitive, "a decision with NO outcome must not be described as accepted — every reader returns its empty default for null, which would otherwise render it as 'accepted, but no agent ran'");
        note.ShouldNotContain("#5 plan", Case.Insensitive, "only agent-staging verbs are described — a plan is not one");
    }

    [Fact]
    public void A_terminating_verdict_is_not_padded_with_the_per_verb_breakdown()
    {
        // The breakdown is diagnostic for a loop. A run that reached a stop is scored on shipping, and appending the
        // walk there would bury the actual verdict under noise on every ordinary pass.
        var (_, note) = SupervisorTrajectoryScore.Score(new SupervisorTrajectoryResult
        {
            Kinds = new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Stop },
            ReachedStop = true,
            HitTurnCap = false,
            Ledger = new[]
            {
                LedgerEntry(SupervisorDecisionKinds.Plan, 1, "{}"),
                LedgerEntry(SupervisorDecisionKinds.Spawn, 2, """{"agentRunIds":[],"agentCount":0,"spawn":"rejected","reason":"x"}"""),
                LedgerEntry(SupervisorDecisionKinds.Stop, 3, "{}"),
            },
        });

        note.ShouldNotContain("What each staging verb did", Case.Insensitive);
    }

    private static SupervisorPriorDecision LedgerEntry(string kind, long sequence, string? outcomeJson) => new()
    {
        Id = Guid.NewGuid(), Sequence = sequence, DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = "{}", OutcomeJson = outcomeJson,
    };

    /// <summary>
    /// The harness must not INVENT units a spawn never named. It used to substitute a hardcoded ["s1","s2"] and then
    /// report both as SUCCEEDED — so the model was shown success for units absent from its own plan while its real
    /// units stayed unfinished, and spawning again was the rational response. That is the documented plan→spawn×7
    /// loop, and it was manufactured by the measuring instrument, not by the brain.
    ///
    /// <para>Production stages ZERO for a spawn that names nothing (StageAgentsAndParkAsync → note: "no subtasks to
    /// spawn"). The harness must say the same thing, or the eval scores the model against a fiction.</para>
    /// </summary>
    [Fact]
    public void A_spawn_that_names_no_subtask_stages_nothing_exactly_as_production_does()
    {
        var spawn = new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = """{"subtaskIds":[]}""" };

        var folded = TrajectoryOutcomes.AllSucceeded(spawn, seq: 2);

        SupervisorOutcome.ReadStagedAgentCount(folded.OutcomeJson).ShouldBe(0, "production stages nothing for a spawn that names nothing — a harness that stages two invented units is measuring a fiction");
        SupervisorOutcome.ReadAgentResults(folded.OutcomeJson).ShouldBeEmpty("an invented unit that 'succeeded' is what taught the model its own plan was never finished");
        folded.OutcomeJson.ShouldNotContain("s1", Case.Sensitive, "the hardcoded ids must be gone, not merely unused on the happy path");

        // The refusal, read through the SAME reader the decider's correction block keys on. This lane simulates
        // outcomes rather than driving the real executor, so if it mirrors an older production shape the eval reports
        // a disposition production stopped emitting — which is exactly what happened until this was wired to the
        // executor's own builder.
        SupervisorOutcome.ReadRejectionReason(folded.OutcomeJson).ShouldNotBeNull(
            "production REFUSES an unnamed spawn; a harness still emitting the accepted-with-a-note shape makes the refusal unobservable in the one lane that measures it");
        SupervisorOutcome.ReadRejectionReason(folded.OutcomeJson)!.ShouldContain("named no subtaskIds");
    }

    [Fact]
    public void A_spawn_that_names_its_subtasks_still_folds_them_verbatim()
    {
        // Behaviour-preserving on the path that matters: a well-formed spawn is unaffected.
        var spawn = new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = """{"subtaskIds":["validate-email","send-receipt"]}""" };

        var folded = TrajectoryOutcomes.AllSucceeded(spawn, seq: 2);

        SupervisorOutcome.ReadStagedAgentCount(folded.OutcomeJson).ShouldBe(2);
        folded.OutcomeJson.ShouldContain("validate-email", Case.Sensitive, "the model's OWN ids come back, so its plan and its results agree");
        folded.OutcomeJson.ShouldContain("send-receipt", Case.Sensitive);
    }

    [Fact]
    public async Task A_brain_that_loops_replanning_hits_the_cap_and_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new AlwaysPlanDecider(), maxTurns: 6, CancellationToken.None);

        result.ReachedStop.ShouldBeFalse("a never-stopping brain hits the turn cap");
        SupervisorTrajectoryScore.Score(result).Ok.ShouldBeFalse("looping forever must score a failure — the whole point of a trajectory measure");
    }

    [Fact]
    public async Task A_spawning_brain_is_shown_the_spawn_bound_it_is_being_measured_against()
    {
        // The live 'failure' arc goes plan→spawn×7 into the turn cap on every sampled run, and RUN BOUNDS is the
        // block that exists to head that off. It was absent from EVERY trajectory prompt: the harness built its own
        // turn context and never folded the counters, SupervisorBoundsRecitation returns null while they are all
        // zero, and the gate then scored the model against bounds production would have recited to it.
        var spy = new PromptCapturingDecider();

        await SupervisorTrajectory.RunAsync(spy, SupervisorTrajectoryEnvironments.FailureThenRetry, maxTurns: 5, CancellationToken.None);

        spy.Prompts.Count.ShouldBeGreaterThan(3, "the spy must actually reach the later turns, or this proves nothing");

        spy.Prompts[0].ShouldNotContain("RUN BOUNDS", Case.Sensitive, "turn 0 has an empty tape — nothing is at risk yet, and a fresh run's prompt must stay byte-identical");

        spy.Prompts[^1].ShouldContain("agents spawned", Case.Insensitive,
            "after several spawn waves the model must SEE its spawn count against the cap — production folds this off the same tape, so its absence here means the gate measures a prompt production never renders");
    }

    [Fact]
    public async Task The_stopped_now_verdict_appears_once_a_wave_is_authorized_and_not_before()
    {
        // The trajectory gate scores whether a brain drives to a STOP, and production always tells it what a stop
        // would read as. The gate never did — so it was measuring stop discipline while withholding the one block
        // that informs it. Both directions matter: silent while nothing is staked, present afterwards.
        var spy = new PromptCapturingDecider { PlanWithSubtaskId = "validate-email" };

        await SupervisorTrajectory.RunAsync(spy, SupervisorTrajectoryEnvironments.HappyPath, maxTurns: 4, CancellationToken.None);

        spy.Prompts[0].ShouldNotContain(SupervisorStopNowRecital.Header, Case.Sensitive, "turn 0 has an empty tape — there is no contract to recite and inventing one would be a lie about the run");
        spy.Prompts[1].ShouldNotContain(SupervisorStopNowRecital.Header, Case.Sensitive, "a plan alone stakes nothing; production stakes at AUTHORIZATION, not at planning");

        spy.Prompts[^1].ShouldContain(SupervisorStopNowRecital.Header, Case.Sensitive,
            "once a spawn has staked its units the reducer has a verdict, and production recites it every turn from then on");
    }

    /// <summary>
    /// MIRROR FIDELITY for the FIFTH gate. The trajectory harness builds its own turn context, so every block it
    /// forgets is a block the live gate scores the brain without — the same defect that once deleted RUN BOUNDS and
    /// then failed the model for looping. The harness rendered the recital from the tape's ASSESSMENT alone: no
    /// stage trace, no mode profile, so the stage line could not render at any turn of any arc. That is precisely
    /// the arc it mattered on — <c>ConflictThenResolve</c>, where the conflicted merge integrates nothing and the
    /// brain's next choice is exactly the one the live headline got wrong.
    /// </summary>
    [Fact]
    public async Task A_conflicted_integration_shows_the_brain_the_stage_it_has_not_evidenced()
    {
        var spy = new PromptCapturingDecider { PlanWithSubtaskId = "s1", MergeAfterSpawn = true };

        await SupervisorTrajectory.RunAsync(spy, SupervisorTrajectoryEnvironments.ConflictThenResolve, maxTurns: 5, CancellationToken.None);

        spy.Prompts.Count.ShouldBeGreaterThan(3, "the spy must reach the turn AFTER the conflicted merge, or this proves nothing");

        var afterConflictedMerge = spy.Prompts[3];

        afterConflictedMerge.ShouldContain(SupervisorStopNowRecital.RefusalLead, Case.Sensitive,
            "the harness derives its mode the way a launching run does (CompletionPolicy.DefaultModeFor over the supervisor lane's Enforceable profile), so the DEFAULT cohort is ENFORCED and the wording the live gate must score against is the refusal — not an advisory, and not silence");
        afterConflictedMerge.ShouldContain("requires 1 stage(s) with no evidence — Integrate.", Case.Sensitive,
            "a conflicted merge integrates no branch, so Integrate is unevidenced and the brain has to be told which stage that is before it chooses a stop");
        afterConflictedMerge.ShouldNotContain(SupervisorStopNowRecital.AdvisoryLead, Case.Sensitive,
            "the authority really does refuse this stop — softening it to an advisory trains the brain that a stop it cannot take is merely frowned upon");

        spy.Prompts[1].ShouldNotContain(SupervisorStopNowRecital.RefusalLead, Case.Sensitive,
            "a plan alone stakes nothing, so there is no contract to judge a stage against yet");
    }

    [Fact]
    public async Task The_stopped_now_verdict_tracks_what_the_tape_actually_owes()
    {
        // Both directions on one arc, because the block is only safe to show a model if it MOVES: the failure
        // environment's spawn folds a failed unit (grade Failed — owed), and the retry answers it (grade Passed,
        // delivery attested — settled). The first wiring of this block could never reach the second state: nothing
        // on the tape answered the staked obligations, the recital said "settle what is owed" against a state no
        // action could discharge, and the live gate answered with plan→spawn loops on every arc.
        var spy = new PromptCapturingDecider { PlanWithSubtaskId = "s2", RetryAfterSpawn = true };

        await SupervisorTrajectory.RunAsync(spy, SupervisorTrajectoryEnvironments.FailureThenRetry, maxTurns: 5, CancellationToken.None);

        var afterFailedSpawn = spy.Prompts[2];
        afterFailedSpawn.ShouldContain("UNRESOLVED", Case.Sensitive, "a failed unit is owed work — a settled recital here would bless a stop-as-if-done");
        afterFailedSpawn.ShouldContain("never stop as if done", Case.Insensitive);

        var afterRetrySucceeded = spy.Prompts[^1];
        afterRetrySucceeded.ShouldContain("every contract dimension reads SETTLED", Case.Sensitive,
            "the retry answered the failed unit with a passing, attested result — an owed-forever recital here is an obligation no action can discharge, the exact live regression");
    }

    [Fact]
    public async Task The_agent_results_name_the_units_the_model_itself_planned()
    {
        // The coherence the whole tape rests on. The fan-out folds hardcoded s1/s2, which was survivable only while
        // the plan echo was hardcoded to match. Once the plan echoed the model's REAL ids, the results referenced
        // units it had never planned, its own units stayed permanently unfinished, and the observed answer to that
        // was to spawn again — every failing live arc was the identical plan→spawn×7 shape.
        var spy = new PromptCapturingDecider { PlanWithSubtaskId = "validate-email" };

        await SupervisorTrajectory.RunAsync(spy, SupervisorTrajectoryEnvironments.HappyPath, maxTurns: 4, CancellationToken.None);

        var afterSpawn = spy.Prompts[^1];

        afterSpawn.ShouldContain("agent/validate-email", Case.Sensitive, "the produced branch must belong to a unit the model actually planned");
        afterSpawn.ShouldNotContain("agent/s1", Case.Sensitive, "a result for a unit absent from the model's own plan leaves that plan looking permanently unfinished");
    }

    [Fact]
    public async Task A_retry_reports_the_one_subtask_it_re_ran_and_not_a_second_one()
    {
        // Production stages K=1 for a retry. The harness folded the two-agent SPAWN shape, so the prompt asserted a
        // second subtask had succeeded on a branch while the plan recitation in the SAME prompt listed it as
        // unfinished. A model that believed the results block — the one tagged "act on THESE results" — merged early
        // and was scored down for it. The gate was failing models for reading the prompt correctly.
        var spy = new PromptCapturingDecider { RetryAfterSpawn = true };

        await SupervisorTrajectory.RunAsync(spy, SupervisorTrajectoryEnvironments.FailureThenRetry, maxTurns: 5, CancellationToken.None);

        var afterRetry = spy.Prompts[^1];

        afterRetry.ShouldContain("retried s2", Case.Insensitive, "the retry must report the subtask the model actually named");
        afterRetry.ShouldNotContain("agent 2:", Case.Sensitive, "a retry re-runs ONE unit — a second agent result is a claim production never makes");
    }

    [Fact]
    public async Task A_plan_outcome_echoes_the_subtasks_the_model_actually_authored()
    {
        // The outcome used to be a hardcoded ["s1","s2"], so every turn after the model planned showed an outcome
        // naming ids absent from the payload the model had just written — the prompt contradicting itself on the
        // run's most basic fact.
        var spy = new PromptCapturingDecider { PlanWithSubtaskId = "validate-email" };

        await SupervisorTrajectory.RunAsync(spy, SupervisorTrajectoryEnvironments.HappyPath, maxTurns: 3, CancellationToken.None);

        spy.Prompts[^1].ShouldContain("validate-email", Case.Sensitive, "the plan outcome must echo the model's own subtask id");
        spy.Prompts[^1].ShouldNotContain("\"planned\":[\"s1\",\"s2\"]", Case.Sensitive, "a fixed echo names units the model never planned");
    }

    [Fact]
    public async Task A_re_planning_brain_is_shown_the_no_progress_streak()
    {
        // The other half of the same block, and the one with teeth for a loop: re-planning lands no settled evidence,
        // so the streak climbs. Deliberately a SEPARATE arc from the spawn case above — a spawn that yields a
        // succeeded agent resets the streak by design, so asserting both on one trajectory would be asserting
        // something false about how progress is counted.
        var spy = new PromptCapturingDecider { PlanForever = true };

        await SupervisorTrajectory.RunAsync(spy, SupervisorTrajectoryEnvironments.HappyPath, maxTurns: 5, CancellationToken.None);

        spy.Prompts[^1].ShouldContain("no-progress decisions", Case.Insensitive,
            "a brain that has re-planned four times running has produced nothing, which is exactly the state the recitation names — and the state the live gate keeps failing runs for reaching");
    }

    [Fact]
    public async Task A_brain_that_stops_immediately_without_shipping_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new StopImmediatelyDecider(), maxTurns: 6, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it did stop — but with nothing shipped");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("quitting before any merge/resolve must fail — a stop is only good after shipping");
        note.ShouldContain("WITHOUT shipping");
    }

    [Fact]
    public async Task A_brain_that_merges_without_planning_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new MergeFirstDecider(), maxTurns: 6, CancellationToken.None);

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("merging with nothing planned is shipping out of nothing — not driving to completion");
        note.ShouldContain("PLANNING");
    }

    [Fact]
    public async Task A_brain_that_plans_then_merges_without_doing_the_work_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new PlanThenMergeDecider(), maxTurns: 6, CancellationToken.None);

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("planning then merging with no spawn/retry/resolve is shipping out of nothing");
        note.ShouldContain("DOING THE WORK");
    }

    [Fact]
    public async Task A_brain_that_ships_then_churns_on_re_spawns_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new ChurningDecider(), maxTurns: 12, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it does eventually stop — but only after wasteful re-spawning");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("re-staging the SAME units five times over is non-converging churn");
        note.ShouldContain("churning");
        note.ShouldContain("s1 ×5", Case.Insensitive, "the verdict names WHICH unit was re-attempted and how often — a bare count gave the reader no move");
    }

    /// <summary>
    /// The calibration this cap was BUILT for is a brain that fans its whole plan out in one wide spawn. Run
    /// 33814929951's persistent-conflict lane staged its dependency chain SERIALLY instead — a distinct planned unit
    /// per turn, each waiting on the one before — and the flat "staged work 5 times (&gt; 4)" ceiling scored five
    /// honest FIRST attempts as churn. Staging distinct planned units one at a time is a legitimate strategy; churn
    /// is re-staging the SAME unit without progress. The scorer must be able to tell the two apart, or the gate
    /// measures fan-out shape rather than judgment.
    /// </summary>
    [Fact]
    public async Task A_brain_that_stages_distinct_planned_units_one_at_a_time_is_not_churn()
    {
        var result = await SupervisorTrajectory.RunAsync(new SerialFanoutDecider(), maxTurns: 12, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.Count(k => k == SupervisorDecisionKinds.Spawn).ShouldBe(5, "the arc under adjudication: five spawns, one planned unit each");

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"five DISTINCT planned units staged serially is a dependency chain, not churn ({note})");
    }

    /// <summary>The other half of the boundary: the same unit staged over and over is churn no matter how few OTHER units the run touched — the property the flat count could not express.</summary>
    [Fact]
    public async Task A_brain_that_restages_one_unit_five_times_is_churn()
    {
        var result = await SupervisorTrajectory.RunAsync(new SingleUnitRestagingDecider(), maxTurns: 12, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — the failure is the road it took, not the ending");

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("one unit handed to an agent five times, with nothing else attempted, is the definition of re-attempting without progress");
        note.ShouldContain("churning");
        note.ShouldContain("only-unit ×5", Case.Insensitive);
    }

    [Fact]
    public async Task A_brain_that_asks_one_question_then_converges_passes()
    {
        var result = await SupervisorTrajectory.RunAsync(new AskThenConvergeDecider(), maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.AskHuman, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"a single legitimate ask_human on the happy path — answered, then converged — must NOT fail the gate ({note})");
    }

    [Fact]
    public async Task A_deadline_already_elapsed_scores_a_budget_failure_not_a_loop()
    {
        using var deadline = new CancellationTokenSource();
        deadline.Cancel();   // a wall-clock deadline that already fired before the first turn

        var result = await SupervisorTrajectory.RunAsync(new AlwaysPlanDecider(), maxTurns: 6, deadline.Token);

        result.ReachedStop.ShouldBeFalse();
        result.HitTurnCap.ShouldBeFalse("a deadline cancellation is NOT a turn-cap loop — the scorer must name the two differently");
        SupervisorTrajectoryScore.Score(result).Note.ShouldContain("time budget");
    }

    [Fact]
    public async Task A_deadline_firing_mid_decision_is_caught_and_scored_not_thrown()
    {
        using var deadline = new CancellationTokenSource();

        // The decider cancels the deadline then throws OperationCanceledException — exactly a wall-clock deadline firing
        // while an HTTP call is in flight. RunAsync must convert that into a clean scored failure, never let it propagate.
        var result = await SupervisorTrajectory.RunAsync(new DeadlineThrowingDecider(deadline), maxTurns: 6, deadline.Token);

        result.ReachedStop.ShouldBeFalse();
        result.HitTurnCap.ShouldBeFalse();
        SupervisorTrajectoryScore.Score(result).Note.ShouldContain("time budget");
    }

    [Fact]
    public async Task A_per_call_HTTP_timeout_propagates_and_is_NOT_mislabeled_as_a_turn_cap()
    {
        // A per-call HttpClient.Timeout throws OperationCanceledException whose token is NOT the trajectory deadline
        // (the deadline never fired). RunAsync must let it propagate — swallowing it would mislabel a slow endpoint as a
        // looping model that "hit the turn cap" (the exact bug the real-model OpenAI wire surfaced).
        await Should.ThrowAsync<OperationCanceledException>(() =>
            SupervisorTrajectory.RunAsync(new PerCallTimeoutDecider(), maxTurns: 6, CancellationToken.None));
    }

    // ── Recovery environments: the brain must RESOLVE a conflict / RETRY a failure before it can ship ────────

    [Fact]
    public async Task A_brain_that_resolves_a_merge_conflict_then_ships_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new ConflictResolvingDecider(), SupervisorTrajectoryEnvironments.ConflictThenResolve, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Resolve, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"resolving the conflict to a VERIFIED resolution and stopping is a sound recovery ({note})");
    }

    [Fact]
    public async Task A_brain_that_gives_up_on_a_merge_conflict_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new ShipNaivelyDecider(), SupervisorTrajectoryEnvironments.ConflictThenResolve, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but on an unresolved conflict");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("stopping on a CONFLICTED merge ships nothing — the conflict was never resolved");
        note.ShouldContain("WITHOUT shipping");
    }

    [Fact]
    public async Task A_brain_that_retries_a_failed_agent_then_ships_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new FailureRetryingDecider(), SupervisorTrajectoryEnvironments.FailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"retrying the failed subtask then merging clean is a sound recovery ({note})");
    }

    [Fact]
    public async Task A_brain_that_merges_over_a_failed_agent_without_retrying_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new ShipNaivelyDecider(), SupervisorTrajectoryEnvironments.FailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but the merge was incomplete (a subtask never succeeded)");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("merging over an un-retried failure integrates nothing clean — it ships no reviewable head");
        note.ShouldContain("WITHOUT shipping");
    }

    // ── Complex multi-turn recovery: persist past an unverified resolution / recover from multiple failures ───

    [Fact]
    public async Task A_brain_that_persists_past_an_unverified_resolution_and_ships_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new ConflictPersistingDecider(), SupervisorTrajectoryEnvironments.ConflictThenUnverifiedThenVerified, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Resolve, SupervisorDecisionKinds.Resolve, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"re-resolving past the unverified reconciliation to a VERIFIED one and stopping is a sound recovery ({note})");
    }

    [Fact]
    public async Task A_brain_that_stops_on_the_first_unverified_resolution_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new ResolveOnceThenStopDecider(), SupervisorTrajectoryEnvironments.ConflictThenUnverifiedThenVerified, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but on an UNVERIFIED resolution");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("accepting the first unverified resolution ships nothing — the reconciliation never passed the build/tests");
        note.ShouldContain("WITHOUT shipping");
    }

    [Fact]
    public async Task A_brain_that_retries_every_failure_then_ships_scores_ok()
    {
        var result = await SupervisorTrajectory.RunAsync(new MultiFailureRecoveringDecider(), SupervisorTrajectoryEnvironments.MultiFailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue();
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"retrying BOTH failed subtasks then merging clean is a sound multi-failure recovery ({note})");
    }

    [Fact]
    public async Task A_brain_that_merges_before_recovering_all_failures_fails()
    {
        var result = await SupervisorTrajectory.RunAsync(new RetryOnceThenMergeDecider(), SupervisorTrajectoryEnvironments.MultiFailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but merged while a subtask was still unrecovered");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("merging before BOTH failures are retried integrates nothing clean — it ships no reviewable head");
        note.ShouldContain("WITHOUT shipping");
    }

    // ── Turn budgets: a cap below an arc's honest minimum measures the budget, not the brain ────────────────

    /// <summary>
    /// Every arc gets the default cap except the two whose recovery costs extra turns — persistent-conflict's second
    /// reconciliation, and conflict's serial staging. Pinned per environment because the numbers are load-bearing:
    /// under a flat 8 both blessed attempts of run 33754366815 earned a CLEAN merge on the persistent-conflict arc,
    /// and run 33931943478's conflict lane earned one on turn 8, and all of them were then scored "never stopped"
    /// for running out of turns.
    /// </summary>
    [Theory]
    [InlineData("happy", SupervisorTrajectory.DefaultMaxTurns)]
    [InlineData("conflict", 10)]
    [InlineData("failure", SupervisorTrajectory.DefaultMaxTurns)]
    [InlineData("multi-failure", SupervisorTrajectory.DefaultMaxTurns)]
    [InlineData("persistent-conflict", 10)]
    public void Each_environment_declares_the_turn_budget_its_own_arc_needs(string scenario, int expected)
    {
        EnvironmentFor(scenario).MaxTurns.ShouldBe(expected);
    }

    /// <summary>
    /// The budget must clear the arc's LONGEST honest path, not just its shortest. Persistent-conflict can ship by
    /// re-merging after the verified resolve — plan→spawn→merge→resolve→resolve→merge→stop, seven turns — and a
    /// brain that takes it must be able to reach `stop`, which is exactly what the flat cap denied.
    /// </summary>
    [Fact]
    public async Task The_persistent_conflict_budget_clears_its_longest_honest_path()
    {
        var environment = SupervisorTrajectoryEnvironments.ConflictThenUnverifiedThenVerified;

        var result = await SupervisorTrajectory.RunAsync(new ConflictPersistingRemergeDecider(), environment, environment.MaxTurns, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("seven turns is an honest path through this arc, and the cap must leave room for it");
        result.Kinds.Count.ShouldBe(7);

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"re-resolving to a VERIFIED reconciliation, re-merging it clean and stopping is a sound recovery ({note})");
    }

    /// <summary>
    /// The conflict arc's longest honest path is the SERIAL one: a brain that walks a dependency chain a unit per
    /// turn spends four turns staging before it can even attempt the integration, and the conflict then costs a
    /// resolve and a re-merge — nine turns, one past the flat default. Run 33931943478's conflict lane earned a
    /// CLEAN merge on turn 8 exactly this way and had no turn left to say stop; it was scored "never stopped", which
    /// measured the budget rather than the brain, on the same arc that passed the run before.
    /// </summary>
    [Fact]
    public async Task The_conflict_budget_clears_its_longest_honest_path()
    {
        var environment = SupervisorTrajectoryEnvironments.ConflictThenResolve;

        var result = await SupervisorTrajectory.RunAsync(new SerialConflictResolvingDecider(), environment, environment.MaxTurns, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("staging a dependency chain serially and then reconciling the conflict is an honest path, and the cap must leave room to say stop at the end of it");
        result.Kinds.Count.ShouldBe(9);

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"four distinct units staged one per turn, the conflict resolved and re-merged clean, then stopped ({note})");
    }

    /// <summary>The environment a real-model scenario name selects — the SAME mapping the live flow test uses, so a budget pinned here is the budget that lane runs under.</summary>
    private static ISupervisorTrajectoryEnvironment EnvironmentFor(string scenario) => scenario switch
    {
        "conflict" => SupervisorTrajectoryEnvironments.ConflictThenResolve,
        "failure" => SupervisorTrajectoryEnvironments.FailureThenRetry,
        "persistent-conflict" => SupervisorTrajectoryEnvironments.ConflictThenUnverifiedThenVerified,
        "multi-failure" => SupervisorTrajectoryEnvironments.MultiFailureThenRetry,
        _ => SupervisorTrajectoryEnvironments.HappyPath,
    };

    // ── Environment fidelity: a re-dispatch must be able to SUCCEED, or the fixture fabricates a fail-loop ───

    /// <summary>
    /// The failure environment must fail the FIRST spawn ONLY. Folding EVERY spawn through the one-failed shape
    /// manufactures a fresh failure for each re-dispatch, so the brain is punished for a production-legitimate
    /// recovery and the arc cannot terminate — the same disease the multi-failure sibling was fixed for, observed
    /// again live on run 33723910434 as plan→spawn→retry→spawn→retry→spawn→retry→spawn into the turn cap.
    /// </summary>
    [Fact]
    public void The_failure_environment_fails_only_the_first_spawn_and_lets_a_re_dispatch_succeed()
    {
        var environment = SupervisorTrajectoryEnvironments.FailureThenRetry;
        var spawn = new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = ScriptedPayload(SupervisorDecisionKinds.Spawn) };

        var first = environment.Fold(spawn, 1, Array.Empty<SupervisorPriorDecision>());
        var second = environment.Fold(spawn, 2, new[] { first });

        StatusesOf(first).ShouldBe(new[] { "Succeeded", "Failed" }, "the FIRST spawn leaves exactly one unit for the brain to recover");
        StatusesOf(second).ShouldBe(new[] { "Succeeded", "Succeeded" }, "a re-dispatch is a legitimate fresh attempt and must be able to succeed — otherwise every re-spawn folds a NEW failure and no arc can ever end");
    }

    /// <summary>
    /// The same property end-to-end: a brain that recovers by RE-SPAWNING the failed unit rather than calling the
    /// retry verb reaches a terminal stop and ships. Before the guard every re-spawn folded a fresh failure, so this
    /// arc could only ever exhaust the turn cap.
    /// </summary>
    [Fact]
    public async Task A_brain_that_recovers_by_re_spawning_ships_instead_of_looping()
    {
        var result = await SupervisorTrajectory.RunAsync(new RespawnRecoveringDecider(), SupervisorTrajectoryEnvironments.FailureThenRetry, maxTurns: 8, CancellationToken.None);

        result.HitTurnCap.ShouldBeFalse("a re-dispatch that can succeed lets the arc terminate");
        result.Kinds.ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop });

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"re-spawning the failed unit then merging clean is a sound recovery ({note})");
    }

    /// <summary>
    /// A re-dispatch only counts when it NAMES the unit that failed. A brain that re-spawns the subtask which
    /// ALREADY succeeded has recovered nothing, and a merge behind it must stay INCOMPLETE — otherwise the bar reads
    /// the TALLY of spawns instead of their TARGET, and a run ships over an untouched failure.
    /// </summary>
    [Theory]
    [InlineData("failure")]
    [InlineData("multi-failure")]
    public async Task A_brain_that_re_spawns_the_unit_that_already_succeeded_does_not_ship(string scenario)
    {
        var environment = scenario == "failure" ? SupervisorTrajectoryEnvironments.FailureThenRetry : SupervisorTrajectoryEnvironments.MultiFailureThenRetry;

        var result = await SupervisorTrajectory.RunAsync(new WrongUnitRespawnDecider(), environment, maxTurns: 8, CancellationToken.None);

        result.ReachedStop.ShouldBeTrue("it stops — but it re-dispatched the wrong unit");
        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeFalse("re-spawning a unit that already succeeded recovers nothing — the failure is still unintegrated");
        note.ShouldContain("WITHOUT shipping");
    }

    /// <summary>
    /// A REFUSED spawn must not spend the arc's failure slot. The injection fires only while no spawn has happened
    /// yet, and the counter it asked counted EVERY prior spawn — including one production refused for naming no
    /// unit. So the malformed turn-1 spawn of run 33931943478 burned the slot, the real spawn came back all-green,
    /// nothing was ever owed, and the merge gate (which reads as unrecovered on a tape that owed nothing) held shut
    /// forever: both attempts of the multi-failure lane ran plan→spawn→spawn→merge→merge→merge into the turn cap.
    /// A spawn that dispatched ZERO agents is not an attempt at the work, and the arc must still get to fail.
    /// </summary>
    [Fact]
    public async Task A_refused_first_spawn_does_not_spend_the_arcs_failure_slot()
    {
        var environment = SupervisorTrajectoryEnvironments.MultiFailureThenRetry;

        var result = await SupervisorTrajectory.RunAsync(new RefusedThenRecoveringDecider(), environment, environment.MaxTurns, CancellationToken.None);

        result.Kinds.ShouldBe(new[]
        {
            SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Spawn, SupervisorDecisionKinds.Spawn,
            SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Retry, SupervisorDecisionKinds.Merge, SupervisorDecisionKinds.Stop,
        }, "the REAL spawn — the first one that dispatched anything — must be the one that carries the double failure, so the brain has something to recover");

        var (ok, note) = SupervisorTrajectoryScore.Score(result);
        ok.ShouldBeTrue($"recovering both injected failures by name and merging clean is a sound arc, and a refused spawn before it changes nothing ({note})");
    }

    /// <summary>
    /// A tape that never OWED a unit must not read as unrecovered. The gate returned <c>recovered &amp;&amp; owed.Count == 0</c>
    /// with <c>recovered</c> flipped only by a re-dispatch overlapping something owed, so a run where nothing ever
    /// failed was VACUOUSLY false — every merge behind it came back Incomplete no matter what the brain did. That is
    /// the second half of run 33931943478's turn-cap loop: once the refused spawn had eaten the failure injection,
    /// no action existed that could open the merge.
    /// </summary>
    [Fact]
    public void A_merge_behind_a_wave_that_owed_nothing_integrates_instead_of_reading_as_unrecovered()
    {
        var environment = SupervisorTrajectoryEnvironments.MultiFailureThenRetry;
        var spawn = new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = ScriptedPayload(SupervisorDecisionKinds.Spawn) };
        var allGreen = TrajectoryOutcomes.AllSucceeded(spawn, seq: 1);

        TrajectoryOutcomes.HasRecoveredEveryFailedUnit(new[] { allGreen }).ShouldBeTrue("no unit was ever owed, so none is outstanding — reading that as 'never recovered' is a gate no action can open");

        var merge = environment.Fold(new SupervisorDecision { Kind = SupervisorDecisionKinds.Merge, PayloadJson = "{}" }, 2, new[] { allGreen });

        SupervisorOutcome.ReadIntegration(merge.OutcomeJson)!.IntegratedBranch.ShouldNotBeNullOrEmpty("a merge over a wave that failed nothing has a real head to integrate");
    }

    /// <summary>The agent statuses a folded outcome carries, read by the SAME production reader the decider's context is rendered from.</summary>
    private static IReadOnlyList<string> StatusesOf(SupervisorPriorDecision folded) =>
        SupervisorOutcome.ReadAgentResults(folded.OutcomeJson).Select(r => r.Status).ToList();

    // ── Scripted deciders (decide purely from the prior-decision kinds — no model) ──────────────────────────

    /// <summary>
    /// A scripted decision's payload. A SPAWN must NAME the units it is spawning, exactly as a well-formed model
    /// spawn does — the harness no longer invents ids for a spawn that names none, because production stages nothing
    /// for that shape and inventing them is what manufactured the plan→spawn×7 loop. Every other verb keeps its
    /// empty payload.
    /// </summary>
    private static string ScriptedPayload(string kind) =>
        kind == SupervisorDecisionKinds.Spawn ? """{"subtaskIds":["s1","s2"]}""" : "{}";

    /// <summary>A converging brain: plan if nothing planned, spawn if planned-not-spawned, merge if spawned-not-merged, else stop.</summary>
    private sealed class ConvergingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = kind == SupervisorDecisionKinds.Stop ? "{\"outcome\":\"completed\"}" : ScriptedPayload(kind) });
        }
    }

    /// <summary>Spawns forever (the looping shape the gate keeps observing live) and records the USER PROMPT it was handed each turn — the only way to assert on what the model actually saw.</summary>
    private sealed class PromptCapturingDecider : ISupervisorDecider
    {
        public List<string> Prompts { get; } = new();

        /// <summary>Re-plan every turn instead of spawning — the no-evidence loop that climbs the no-progress streak.</summary>
        public bool PlanForever { get; init; }

        /// <summary>Plan → spawn → retry s2, the recovery path the 'failure' arc requires.</summary>
        public bool RetryAfterSpawn { get; init; }

        /// <summary>Plan → spawn → merge → stop, so the captured prompts include the turn that follows an integration attempt.</summary>
        public bool MergeAfterSpawn { get; init; }

        /// <summary>Author a real subtask under this id, so the plan echo can be checked against what the model wrote.</summary>
        public string? PlanWithSubtaskId { get; init; }

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            Prompts.Add(LlmSupervisorDecider.BuildUserPromptForTest(context));

            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();

            // Plan once, then spawn forever — the exact shape the live gate keeps recording (plan→spawn×7).
            // The retry variant stops after its retry: spawning again would fold a FRESH failure over the unit the
            // retry just answered, and the stopped-now verdict would (correctly) read owed again — the direction
            // test needs the tape to END on the answered state to observe the settled arm.
            var kind = PlanForever ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : RetryAfterSpawn && kinds.Contains(SupervisorDecisionKinds.Retry) ? SupervisorDecisionKinds.Stop
                : RetryAfterSpawn && kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Retry
                : MergeAfterSpawn && kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Stop
                : MergeAfterSpawn && kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Spawn;

            var payload =
                kind == SupervisorDecisionKinds.Stop ? """{"summary":"retried and shipped"}"""
                : kind == SupervisorDecisionKinds.Retry ? """{"subtaskId":"s2"}"""
                : kind == SupervisorDecisionKinds.Merge ? ScriptedPayload(kind)
                : PlanWithSubtaskId is not { } id ? ScriptedPayload(kind)   // an unnamed spawn would now stage nothing, exactly as production refuses it
                : kind == SupervisorDecisionKinds.Plan ? $$"""{"subtasks":[{"id":"{{id}}","title":"t","instruction":"i"}]}"""
                : $$"""{"subtaskIds":["{{id}}"]}""";   // a spawn names the units it dispatches, as a real one does

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = payload });
        }
    }

    private sealed class AlwaysPlanDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = "{}" });
    }

    private sealed class StopImmediatelyDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{\"outcome\":\"completed\"}" });
    }

    /// <summary>Ships out of nothing: merge as the first verb, then stop — no plan, no work.</summary>
    private sealed class MergeFirstDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind = !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>Plans then merges with no spawn/retry/resolve in between — shipping out of nothing despite a plan.</summary>
    private sealed class PlanThenMergeDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>Ships, but only after staging work far more than the happy path needs (5 spawns) — non-converging churn.</summary>
    private sealed class ChurningDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : kinds.Count(k => k == SupervisorDecisionKinds.Spawn) < 5 ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>
    /// The SERIAL fan-out shape run 33814929951 was failed for: plan five units, then stage them ONE PER TURN — a
    /// dependency chain walked in order, every spawn naming a unit no earlier spawn touched.
    /// </summary>
    private sealed class SerialFanoutDecider : ISupervisorDecider
    {
        private static readonly string[] Units = { "u1", "u2", "u3", "u4", "u5" };

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();

            if (!kinds.Contains(SupervisorDecisionKinds.Plan))
                return Decision(SupervisorDecisionKinds.Plan, $$"""{"subtasks":[{{string.Join(",", Units.Select(u => $$"""{"id":"{{u}}","title":"{{u}}","instruction":"do {{u}}"}"""))}}]}""");

            var staged = kinds.Count(k => k == SupervisorDecisionKinds.Spawn);
            if (staged < Units.Length) return Decision(SupervisorDecisionKinds.Spawn, $$"""{"subtaskIds":["{{Units[staged]}}"]}""");

            return !kinds.Contains(SupervisorDecisionKinds.Merge)
                ? Decision(SupervisorDecisionKinds.Merge, "{}")
                : Decision(SupervisorDecisionKinds.Stop, "{\"outcome\":\"completed\"}");
        }

        private static Task<SupervisorDecision> Decision(string kind, string payload) => Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = payload });
    }

    /// <summary>The mirror image of <see cref="SerialFanoutDecider"/>: five spawns that all name the SAME unit. Same verb count, opposite judgment.</summary>
    private sealed class SingleUnitRestagingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : kinds.Count(k => k == SupervisorDecisionKinds.Spawn) < 5 ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            var payload =
                kind == SupervisorDecisionKinds.Plan ? """{"subtasks":[{"id":"only-unit","title":"t","instruction":"i"}]}"""
                : kind == SupervisorDecisionKinds.Spawn ? """{"subtaskIds":["only-unit"]}"""
                : kind == SupervisorDecisionKinds.Stop ? "{\"outcome\":\"completed\"}"
                : "{}";

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = payload });
        }
    }
    /// <summary>A cautious-but-correct brain: plan, ask ONE question, then spawn → merge → stop. The ask is answered (the harness folds a real reply) and the scorer must tolerate the detour.</summary>
    private sealed class AskThenConvergeDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.AskHuman) ? SupervisorDecisionKinds.AskHuman
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>Simulates a wall-clock deadline firing mid-decision: cancels the deadline source, then throws — RunAsync must catch it and score a budget failure, not propagate.</summary>
    private sealed class DeadlineThrowingDecider : ISupervisorDecider
    {
        private readonly CancellationTokenSource _deadline;
        public DeadlineThrowingDecider(CancellationTokenSource deadline) { _deadline = deadline; }

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            _deadline.Cancel();
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>Simulates an HttpClient per-call timeout: throws OperationCanceledException WITHOUT the trajectory deadline being cancelled — RunAsync must let it propagate, not swallow it as a turn-cap.</summary>
    private sealed class PerCallTimeoutDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            throw new OperationCanceledException();
    }

    /// <summary>A conflict-aware brain: plan→spawn→merge, and when that merge CONFLICTS, spawn a resolver (→verified) and stop on the accepted resolution. Reads the ledger outcomes, not just kinds.</summary>
    private sealed class ConflictResolvingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var conflicted = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is { IsConflicted: true });
            var verifiedResolve = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Resolve && SupervisorOutcome.ReadResolutionVerdict(d.OutcomeJson) == SupervisorResolutionVerdict.Verified);

            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : conflicted && !verifiedResolve ? SupervisorDecisionKinds.Resolve
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>A failure-aware brain: plan→spawn, and when an agent FAILED, retry the failed subtask before merging clean and stopping. Reads the ledger's agent results.</summary>
    private sealed class FailureRetryingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var hasFailure = priors.Any(d => SupervisorDecisionKinds.StagesAgents(d.DecisionKind) && SupervisorOutcome.ReadAgentResults(d.OutcomeJson).Any(r => string.Equals(r.Status, "Failed", StringComparison.OrdinalIgnoreCase)));

            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : hasFailure && !kinds.Contains(SupervisorDecisionKinds.Retry) ? SupervisorDecisionKinds.Retry
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>A naive brain that ignores recovery signals: plan→spawn→merge→stop regardless of conflict/failure. In a recovery environment its merge never integrates cleanly, so it ships nothing — the scorer must fail it.</summary>
    private sealed class ShipNaivelyDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>The LONGEST honest path through the persistent-conflict arc: plan→spawn→merge(conflict)→resolve(unverified)→resolve(verified)→merge(clean)→stop. Seven turns — a brain that re-merges after reconciling rather than shipping off the resolve alone.</summary>
    private sealed class ConflictPersistingRemergeDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var merges = kinds.Count(k => k == SupervisorDecisionKinds.Merge);
            var resolves = kinds.Count(k => k == SupervisorDecisionKinds.Resolve);
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : merges == 0 ? SupervisorDecisionKinds.Merge
                : resolves < 2 ? SupervisorDecisionKinds.Resolve
                : merges < 2 ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>A persistent brain: plan→spawn→merge, and while the merge is CONFLICTED and no VERIFIED resolve exists, keep resolving (so it re-resolves past an unverified reconciliation) — then stop on the accepted, verified resolution.</summary>
    private sealed class ConflictPersistingDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var conflicted = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is { IsConflicted: true });
            var verifiedResolve = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Resolve && SupervisorOutcome.ReadResolutionVerdict(d.OutcomeJson) == SupervisorResolutionVerdict.Verified);

            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : conflicted && !verifiedResolve ? SupervisorDecisionKinds.Resolve
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>A brain that ACCEPTS the first resolution: plan→spawn→merge→resolve(once)→stop. Against the unverified-then-verified environment its single resolve is unverified, so it ships nothing — the scorer must fail it.</summary>
    private sealed class ResolveOnceThenStopDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var conflicted = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is { IsConflicted: true });

            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : conflicted && !kinds.Contains(SupervisorDecisionKinds.Resolve) ? SupervisorDecisionKinds.Resolve
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>A thorough recovering brain: plan→spawn, then RETRY until two retries have happened (recovering both failed subtasks), then merge→stop.</summary>
    private sealed class MultiFailureRecoveringDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : kinds.Count(k => k == SupervisorDecisionKinds.Retry) < 2 ? SupervisorDecisionKinds.Retry
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>A brain that recovers by RE-DISPATCHING rather than calling the retry verb: plan→spawn→spawn→merge→stop. Production-legitimate — a fresh attempt at the failed unit can succeed.</summary>
    private sealed class RespawnRecoveringDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : kinds.Count(k => k == SupervisorDecisionKinds.Spawn) < 2 ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }

    /// <summary>A brain that re-dispatches the WRONG unit: plan→spawn(s1,s2)→spawn(s1 only)→merge→stop. s1 already succeeded, so the second spawn recovers nothing the failure needed.</summary>
    private sealed class WrongUnitRespawnDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var spawns = kinds.Count(k => k == SupervisorDecisionKinds.Spawn);
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : spawns < 2 ? SupervisorDecisionKinds.Spawn
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            // The FIRST spawn names both units (so one of them fails); the second names only the one that succeeded.
            var payload = kind == SupervisorDecisionKinds.Spawn && spawns == 1 ? """{"subtaskIds":["s1"]}""" : ScriptedPayload(kind);

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = payload });
        }
    }

    /// <summary>The SERIAL walk through the conflict arc: plan four units, stage them one per turn, merge (conflicted), resolve, re-merge clean, stop — nine turns, the arc's longest honest path.</summary>
    private sealed class SerialConflictResolvingDecider : ISupervisorDecider
    {
        private static readonly string[] Units = { "u1", "u2", "u3", "u4" };

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var staged = kinds.Count(k => k == SupervisorDecisionKinds.Spawn);
            var merges = kinds.Count(k => k == SupervisorDecisionKinds.Merge);

            var (kind, payload) =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? (SupervisorDecisionKinds.Plan, $$"""{"subtasks":[{{string.Join(",", Units.Select(u => $$"""{"id":"{{u}}","title":"{{u}}","instruction":"do {{u}}"}"""))}}]}""")
                : staged < Units.Length ? (SupervisorDecisionKinds.Spawn, $$"""{"subtaskIds":["{{Units[staged]}}"]}""")
                : merges == 0 ? (SupervisorDecisionKinds.Merge, "{}")
                : !kinds.Contains(SupervisorDecisionKinds.Resolve) ? (SupervisorDecisionKinds.Resolve, "{}")
                : merges < 2 ? (SupervisorDecisionKinds.Merge, "{}")
                : (SupervisorDecisionKinds.Stop, """{"outcome":"completed"}""");

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = payload });
        }
    }

    /// <summary>
    /// The live multi-failure shape of run 33931943478: the first spawn is REFUSED — the model emitted kind "spawn"
    /// with no spawn object at all, which is schema-valid and so reaches the executor, which names no unit and
    /// stages nothing — and the brain then spawns properly and RETRIES BY NAME whatever that wave left failed,
    /// merging until the integration comes back clean. Deliberately ledger-aware: a decider that retried on a fixed
    /// schedule would open the merge gate on its retry TALLY and never notice whether the failure was injected.
    /// </summary>
    private sealed class RefusedThenRecoveringDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var priors = context.PriorDecisions;
            var kinds = priors.Select(d => d.DecisionKind).ToList();
            var spawns = kinds.Count(k => k == SupervisorDecisionKinds.Spawn);
            var owed = UnitsStillOwed(priors);
            var integrated = priors.Any(d => d.DecisionKind == SupervisorDecisionKinds.Merge && SupervisorOutcome.ReadIntegration(d.OutcomeJson) is { IntegratedBranch: { Length: > 0 } });

            var (kind, payload) =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? (SupervisorDecisionKinds.Plan, """{"subtasks":[{"id":"s1","title":"s1","instruction":"do s1"},{"id":"s2","title":"s2","instruction":"do s2"}]}""")
                : spawns == 0 ? (SupervisorDecisionKinds.Spawn, "{}")
                : spawns == 1 ? (SupervisorDecisionKinds.Spawn, ScriptedPayload(SupervisorDecisionKinds.Spawn))
                : owed.Count > 0 ? (SupervisorDecisionKinds.Retry, $$"""{"subtaskId":"{{owed[0]}}"}""")
                : !integrated ? (SupervisorDecisionKinds.Merge, "{}")
                : (SupervisorDecisionKinds.Stop, """{"outcome":"completed"}""");

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = payload });
        }

        /// <summary>The units a spawn wave graded as NOT succeeded and no retry has since re-run — the brain's own read of what it still owes.</summary>
        private static IReadOnlyList<string> UnitsStillOwed(IReadOnlyList<SupervisorPriorDecision> priors)
        {
            var retried = priors.Where(d => d.DecisionKind == SupervisorDecisionKinds.Retry).Select(d => SupervisorOutcome.ReadRetrySubtaskId(d.PayloadJson) ?? "").ToHashSet(StringComparer.Ordinal);

            return priors.Where(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).SelectMany(FailedUnitsOf).Where(u => !retried.Contains(u)).Distinct(StringComparer.Ordinal).ToList();
        }

        /// <summary>The units one spawn left failed — its folded results are positional with the ids it named, exactly as every fold builds them.</summary>
        private static IEnumerable<string> FailedUnitsOf(SupervisorPriorDecision spawn)
        {
            var results = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson);

            return SupervisorOutcome.ReadSpawnSubtaskIds(spawn.PayloadJson).Where((_, i) => i < results.Count && results[i].Status != "Succeeded");
        }
    }

    /// <summary>A hasty brain that retries only ONCE then merges: plan→spawn→retry→merge→stop. Against the multi-failure environment one failure is still unrecovered at merge, so it integrates nothing clean — the scorer must fail it.</summary>
    private sealed class RetryOnceThenMergeDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            var kinds = context.PriorDecisions.Select(d => d.DecisionKind).ToList();
            var kind =
                !kinds.Contains(SupervisorDecisionKinds.Plan) ? SupervisorDecisionKinds.Plan
                : !kinds.Contains(SupervisorDecisionKinds.Spawn) ? SupervisorDecisionKinds.Spawn
                : kinds.Count(k => k == SupervisorDecisionKinds.Retry) < 1 ? SupervisorDecisionKinds.Retry
                : !kinds.Contains(SupervisorDecisionKinds.Merge) ? SupervisorDecisionKinds.Merge
                : SupervisorDecisionKinds.Stop;

            return Task.FromResult(new SupervisorDecision { Kind = kind, PayloadJson = ScriptedPayload(kind) });
        }
    }
}

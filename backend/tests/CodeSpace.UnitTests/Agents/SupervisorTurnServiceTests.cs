using System.Text.Json;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor.Executors;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Agents;
using CodeSpace.UnitTests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// 🟢 Unit: the PR-E E2 <see cref="SupervisorTurnService"/> orchestration, driven against an in-memory fake
/// <see cref="ISupervisorDecisionLog"/> (a faithful model of the E1 unique-index dedup + Pending→Running
/// claim + terminal record — the real ledger is pinned over Postgres at the integration tier). Pins:
/// RehydrateFromDecisionLog folds a terminal decision + identifies the in-flight one; turn 1 plans + parks
/// with TurnNumber+1; turn 2 stops + finishes; the no-progress guard forces a clean terminal stop; a replay
/// (re-running a turn whose decision already settled) does NOT re-execute the side effect (no double-plan).
/// </summary>
[Trait("Category", "Unit")]
public class SupervisorTurnServiceTests
{
    private readonly Guid _runId = Guid.NewGuid();
    private readonly Guid _teamId = Guid.NewGuid();

    // ── RehydrateFromDecisionLog: replay terminal, identify in-flight ────────────────

    [Fact]
    public async Task Rehydrate_folds_a_terminal_decision_and_sets_the_turn_number()
    {
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":["a"]}""", """{"planned":["a"]}""");

        var context = await Service(ledger).RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "goal", goalConfig: null, CancellationToken.None);

        context.TurnNumber.ShouldBe(1, "one decided decision → the next turn is turn 1");
        context.PriorDecisions.Count.ShouldBe(1);
        context.PriorDecisions[0].DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        context.PriorDecisions[0].OutcomeJson.ShouldBe("""{"planned":["a"]}""", "the terminal outcome is replayed, not re-derived");
        context.InFlight.ShouldBeNull();
    }

    [Fact]
    public async Task Rehydrate_identifies_the_one_in_flight_decision()
    {
        var ledger = new FakeSupervisorDecisionLog();
        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, """{"subtasks":["a"]}""", """{"planned":["a"]}""");
        ledger.SeedPending(_runId, _teamId, SupervisorDecisionKinds.Stop, """{"reason":"x"}""");

        var context = await Service(ledger).RehydrateFromDecisionLogAsync(_runId, _teamId, "sup", "goal", goalConfig: null, CancellationToken.None);

        context.TurnNumber.ShouldBe(1, "an in-flight (non-terminal) row is NOT a decided decision");
        context.InFlight.ShouldNotBeNull();
        context.InFlight!.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
    }

    // ── The turn loop: turn 1 plan → park; turn 2 stop → finish ──────────────────────

    [Fact]
    public async Task Turn1_plans_and_parks_then_turn2_stops_and_finishes()
    {
        var ledger = new FakeSupervisorDecisionLog();
        var service = Service(ledger);

        var turn1 = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        turn1.IsFinished.ShouldBeFalse("a plan parks for the next turn");
        turn1.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        turn1.NextTurn!.TurnNumber.ShouldBe(1, "the park carries the next turn's number");
        ledger.Rows.Count.ShouldBe(1, "exactly one decision recorded");
        ledger.Rows[0].Status.ShouldBe(SupervisorDecisionStatus.Succeeded);

        var turn2 = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        turn2.IsFinished.ShouldBeTrue("a stop finishes the loop");
        turn2.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        ledger.Rows.Count.ShouldBe(2, "the ledger has exactly two rows in Sequence order");
        ledger.Rows.Select(r => r.DecisionKind).ShouldBe(new[] { SupervisorDecisionKinds.Plan, SupervisorDecisionKinds.Stop });
    }

    // ── No-progress guard → forced terminal stop (fail-closed, counted from the ledger) ──

    [Fact]
    public async Task The_no_progress_guard_forces_a_clean_terminal_stop()
    {
        var ledger = new FakeSupervisorDecisionLog();

        // Seed MaxNoProgressDecisions result-less plan decisions → the no-progress guard trips → the decider is never asked.
        for (var i = 0; i < SupervisorLane.DefaultMaxNoProgressDecisions; i++)
            ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, $$"""{"turn":{{i}}}""", "{}");

        // A decider that would NEVER stop on its own — proving the bound, not the decider, terminates.
        var service = new SupervisorTurnService(ledger, new AlwaysPlanDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        result.IsFinished.ShouldBeTrue("the no-progress guard forces a terminal stop");
        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        result.TerminalReason.ShouldBe(SupervisorStopReasons.NoProgress);
    }

    // ── Governance DENY → force-STOP=GovernanceDenied, no agent staged (the fail-closed branch) ──

    [Theory]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    public void A_governance_denied_side_effecting_decision_force_stops_and_stages_no_agent(string kind)
    {
        // Drive the REAL Deny wiring (SupervisorTurnService.GateSideEffectingDecision) end-to-end, not its two
        // ingredients in isolation. The branch is unreachable from operator config today (ParseApprovalPolicy
        // clamps every unknown policy to None), so we inject the unmapped policy directly into the gate's context
        // — the same forward-compat exposure a future irreversible/merge-PR policy would open. Asserts the gate
        // turns the denied side effect into a force-STOP carrying the GovernanceDenied reason and stages NO agent.
        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new StubSupervisorDecider(), executor, db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var context = new SupervisorTurnContext { Goal = "goal", TurnNumber = 0, ApprovalPolicy = (SupervisorApprovalPolicy)999 };
        var spawn = new SupervisorDecision { Kind = kind, PayloadJson = """{"subtaskIds":["a","b"]}""" };

        var gated = service.GateSideEffectingDecision(context, spawn);
        var result = SupervisorTurnService.BuildResult(context, gated, SupervisorExecution.Synchronous("{}"));

        result.IsFinished.ShouldBeTrue("an unmapped policy → Confined → the gate DENIES → the turn force-STOPS");
        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        result.TerminalReason.ShouldBe(SupervisorStopReasons.GovernanceDenied, "the DENY branch surfaces the distinct, operator-legible governance-refused reason");
        executor.Calls.ShouldBe(0, "the gate refused BEFORE any execute — no agent was staged");
    }

    // ── Resolver loop #379 S4: a resolve is irreversible → HITL even under the autonomous policy ──

    [Fact]
    public void A_resolve_parks_for_approval_under_the_autonomous_policy_but_a_spawn_does_not()
    {
        // The safety floor wired end-to-end through the REAL gate: under None (autonomous) a spawn runs unchanged,
        // but a resolve — which dispatches an agent to autonomously RE-MERGE code — escalates to a human approval
        // card (it parks), because GateSideEffectingDecision passes irreversible=IsIrreversible(kind) for resolve.
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new StubSupervisorDecider(), new CountingExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var context = new SupervisorTurnContext { Goal = "goal", SupervisorRunId = _runId, TeamId = _teamId, NodeId = "sup", TurnNumber = 1, ApprovalPolicy = SupervisorApprovalPolicy.None, ConversationId = Guid.NewGuid() };

        var spawn = service.GateSideEffectingDecision(context, new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = """{"subtaskIds":["a"]}""" });
        spawn.Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "a normal spawn under the autonomous policy is NOT gated — it runs unchanged");

        var resolve = service.GateSideEffectingDecision(context, new SupervisorDecision { Kind = SupervisorDecisionKinds.Resolve, PayloadJson = "{}" });
        resolve.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "a resolve under the SAME autonomous policy is rewritten into an approval card — it parks for a human before any autonomous re-merge");
    }

    // ── Resolver loop #379 S5: a terminal stop surfaces the run's final integrated branch (node output) ──

    [Fact]
    public void BuildResult_on_a_terminal_stop_surfaces_the_final_integrated_branch()
    {
        // Part A wiring: on a terminal stop, BuildResult folds the run's final reviewable branch off the tape onto
        // the turn result, so the node emits it as `integratedBranch` for a downstream git.open_pr. Here the tape's
        // latest integration is a VERIFIED resolution → its OWN tested branch is the run's reconciled head.
        var resolverResult = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Summary = $"done {SupervisorResolverRecipe.TestsPassedMarker}", ProducedBranch = "codespace/resolve/final" };
        var resolve = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 3, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
            OutcomeJson = JsonSerializer.Serialize(new { agentRunIds = new[] { resolverResult.AgentRunId }, agentCount = 1, agentResults = new[] { resolverResult } }, AgentJson.Options),
        };
        var context = new SupervisorTurnContext { Goal = "goal", TurnNumber = 4, PriorDecisions = new[] { resolve } };

        var result = SupervisorTurnService.BuildResult(context, new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{}" }, SupervisorExecution.Synchronous("{}"));

        result.IsFinished.ShouldBeTrue();
        result.IntegratedBranch.ShouldBe("codespace/resolve/final", "the node surfaces the run's final reviewable branch so a downstream open_pr can bind it directly");
    }

    [Fact]
    public void BuildResult_carries_no_integrated_branch_when_the_tape_has_no_clean_integration()
    {
        var context = new SupervisorTurnContext { Goal = "goal", TurnNumber = 1, PriorDecisions = Array.Empty<SupervisorPriorDecision>() };

        var result = SupervisorTurnService.BuildResult(context, new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{}" }, SupervisorExecution.Synchronous("{}"));

        result.IsFinished.ShouldBeTrue();
        result.IntegratedBranch.ShouldBeNull("a run that never produced a clean integration surfaces no branch (the node emits an empty string)");
        result.RepositoryBranches.ShouldBeEmpty("a single-repo / no-integration run surfaces no per-repo branches either");
    }

    [Fact]
    public void BuildResult_on_a_terminal_stop_surfaces_the_per_repo_integrated_branches_for_a_multi_repo_run()
    {
        // S7-D1 wiring: a MULTI-repo run has no single integratedBranch (each repo integrates on its own axis), so
        // BuildResult folds the per-repo branches off the latest clean multi-repo merge onto RepositoryBranches — the
        // node surfaces them as `repositoryBranches` for a downstream per-repo PR-open.
        var webId = Guid.NewGuid();
        var apiId = Guid.NewGuid();
        var merge = new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(), Sequence = 2, DecisionKind = SupervisorDecisionKinds.Merge, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
            OutcomeJson = JsonSerializer.Serialize(new
            {
                integration = new
                {
                    status = "Clean",
                    repositories = new[]
                    {
                        new { repositoryId = webId, alias = "web", status = "Clean", integratedBranch = "codespace/integration/run/turn1" },
                        new { repositoryId = apiId, alias = "api", status = "Clean", integratedBranch = "codespace/integration/run/turn1" },
                    },
                },
            }, AgentJson.Options),
        };
        var context = new SupervisorTurnContext { Goal = "goal", TurnNumber = 2, PriorDecisions = new[] { merge } };

        var result = SupervisorTurnService.BuildResult(context, new SupervisorDecision { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{}" }, SupervisorExecution.Synchronous("{}"));

        result.IntegratedBranch.ShouldBeNull("a multi-repo run has no single integrated branch");
        result.RepositoryBranches.Count.ShouldBe(2, "each cleanly-integrated repo surfaces its own branch");
        result.RepositoryBranches.Single(b => b.Alias == "api").RepositoryId.ShouldBe(apiId);
    }

    [Fact]
    public void Finish_emits_the_repositoryBranches_output_ONLY_for_a_multi_repo_run()
    {
        // S7-D1 node guard: a multi-repo run surfaces `repositoryBranches`; a single-repo run OMITS the key entirely so
        // its output bag is byte-identical to pre-S7-D1 (mirrors the existing integratedBranch-only single-repo output).
        var multi = AgentSupervisorNode.Finish(NullLogger.Instance, SupervisorTurnResult.Finished("stop", "done", integratedBranch: null, repositoryBranches: new[]
        {
            new SupervisorRepositoryBranch { RepositoryId = Guid.NewGuid(), Alias = "web", SourceBranch = "codespace/integration/run/turn1" },
        })).Outputs;

        multi.ContainsKey("repositoryBranches").ShouldBeTrue("a multi-repo run surfaces its per-repo integrated branches");
        multi["repositoryBranches"].GetArrayLength().ShouldBe(1);

        var single = AgentSupervisorNode.Finish(NullLogger.Instance, SupervisorTurnResult.Finished("stop", "done", integratedBranch: "codespace/integration/x")).Outputs;

        single.ContainsKey("repositoryBranches").ShouldBeFalse("a single-repo run omits the key — its output bag is byte-identical to pre-S7-D1");
        single["integratedBranch"].GetString().ShouldBe("codespace/integration/x");
    }

    // ── L4 P1: a terminal stop's MODEL-authored acceptance gates the verdict + branch ──

    [Fact]
    public void BuildResult_withholds_the_branch_and_reports_failure_when_the_stop_grade_FAILED()
    {
        // The stop's execution outcome carries a FAILED folded grade (the model's definition of done did not pass) →
        // BuildResult withholds the run's final reviewable head + carries AcceptancePassed=false (node → AcceptanceFailed).
        var context = StopContextWithFinalBranch();

        var stopOutcome = SupervisorOutcome.AppendAcceptanceGrade("""{"stopped":true}""", passed: false, "model-check-failed");
        var result = SupervisorTurnService.BuildResult(context, StopDecision(), SupervisorExecution.Synchronous(stopOutcome));

        result.AcceptancePassed.ShouldBe(false);
        result.IntegratedBranch.ShouldBeNull("a failed model definition-of-done withholds the reviewable head — no verified branch to ship");
        result.RepositoryBranches.ShouldBeEmpty();
    }

    [Fact]
    public void BuildResult_surfaces_the_branch_and_reports_pass_when_the_stop_grade_PASSED()
    {
        var context = StopContextWithFinalBranch();

        var stopOutcome = SupervisorOutcome.AppendAcceptanceGrade("""{"stopped":true}""", passed: true, "tests-passed");
        var result = SupervisorTurnService.BuildResult(context, StopDecision(), SupervisorExecution.Synchronous(stopOutcome));

        result.AcceptancePassed.ShouldBe(true);
        result.IntegratedBranch.ShouldBe("codespace/resolve/final", "a passed grade surfaces the verified head as before");
    }

    [Fact]
    public void BuildResult_leaves_AcceptancePassed_null_and_surfaces_the_branch_when_no_grade_was_folded()
    {
        // The dominant case (no model acceptance authored): no acceptanceGrade on the stop outcome → AcceptancePassed
        // null → the node reports Completed + the branch surfaces, byte-identical to before this slice.
        var context = StopContextWithFinalBranch();

        var result = SupervisorTurnService.BuildResult(context, StopDecision(), SupervisorExecution.Synchronous("""{"stopped":true}"""));

        result.AcceptancePassed.ShouldBeNull();
        result.IntegratedBranch.ShouldBe("codespace/resolve/final");
    }

    [Theory]
    [InlineData(null, "Completed")]   // no model acceptance authored → byte-identical Completed
    [InlineData(true, "Completed")]
    [InlineData(false, "AcceptanceFailed")]
    public void Finish_maps_the_acceptance_verdict_to_the_status_output(bool? acceptancePassed, string expectedStatus)
    {
        var outputs = AgentSupervisorNode.Finish(NullLogger.Instance, SupervisorTurnResult.Finished("stop", "done", integratedBranch: "b", acceptancePassed: acceptancePassed)).Outputs;

        outputs["status"].GetString().ShouldBe(expectedStatus);
    }

    [Fact]
    public async Task A_full_turn_stop_with_a_model_acceptance_that_FAILS_grades_once_reports_failure_and_withholds_the_branch()
    {
        var ledger = SeedRunWithCleanMerge();
        var grader = new FakeAcceptanceGrader(new BenchmarkGrade { Passed = false, Detail = "model-check-failed" });
        var service = ServiceWith(ledger, new StopWithAcceptanceDecider("npm", "test"), grader);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", null, GoalConfigWithRepo(), CancellationToken.None);

        result.IsFinished.ShouldBeTrue();
        result.AcceptancePassed.ShouldBe(false);
        result.IntegratedBranch.ShouldBeNull("the failed model DoD withholds the reviewable head end-to-end");
        grader.CallCount.ShouldBe(1, "the model command is graded exactly once against the run's final branch");
        grader.LastCall!.Value.Branch.ShouldBe("codespace/integration/final");
        grader.LastCall.Value.Command.ShouldBe(new[] { "npm", "test" });
        SupervisorOutcome.ReadAcceptanceGradePassed(StopRowOutcome(ledger)).ShouldBe(false, "the verdict is folded durably onto the stop row");
    }

    [Fact]
    public async Task A_full_turn_stop_with_a_model_acceptance_that_PASSES_reports_completed_and_surfaces_the_branch()
    {
        var ledger = SeedRunWithCleanMerge();
        var grader = new FakeAcceptanceGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var service = ServiceWith(ledger, new StopWithAcceptanceDecider("npm", "test"), grader);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", null, GoalConfigWithRepo(), CancellationToken.None);

        result.AcceptancePassed.ShouldBe(true);
        result.IntegratedBranch.ShouldBe("codespace/integration/final");
        grader.CallCount.ShouldBe(1);
    }

    [Fact]
    public async Task A_full_turn_stop_with_NO_model_acceptance_never_grades_and_is_byte_identical()
    {
        var ledger = SeedRunWithCleanMerge();
        var grader = new FakeAcceptanceGrader();
        var service = ServiceWith(ledger, new StopWithAcceptanceDecider(/* no command */), grader);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", null, GoalConfigWithRepo(), CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull("no model definition-of-done → no grade → the node reports Completed");
        result.IntegratedBranch.ShouldBe("codespace/integration/final");
        grader.CallCount.ShouldBe(0, "a stop without a model acceptance command never grades");
        SupervisorOutcome.ReadAcceptanceGradePassed(StopRowOutcome(ledger)).ShouldBeNull("the stop outcome carries no acceptanceGrade — byte-identical to pre-slice");
    }

    [Fact]
    public async Task A_full_turn_stop_with_a_model_acceptance_but_no_final_branch_SKIPS_the_grade()
    {
        // A run that produced no single-repo reviewable head (no integration seeded) → nothing to clone+grade → SKIP,
        // never a fail-closed mislabel of a legitimately branchless / analysis-style run.
        var ledger = new FakeSupervisorDecisionLog();
        var grader = new FakeAcceptanceGrader(new BenchmarkGrade { Passed = false, Detail = "would-fail" });
        var service = ServiceWith(ledger, new StopWithAcceptanceDecider("npm", "test"), grader);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", null, GoalConfigWithRepo(), CancellationToken.None);

        result.AcceptancePassed.ShouldBeNull();
        grader.CallCount.ShouldBe(0, "no final branch → the grade is skipped (the run is reported Completed, not falsely AcceptanceFailed)");
    }

    [Fact]
    public async Task A_full_turn_stop_on_a_MULTI_repo_run_grades_every_repo_head_and_withholds_all_on_failure()
    {
        // L4 C2: a multi-repo run grades EACH per-repo head (no longer skipped). The first repo's check fails → the
        // whole all-or-nothing change is not accepted and EVERY per-repo branch is withheld.
        var ledger = SeedRunWithCleanMultiRepoMerge();
        var grader = new FakeAcceptanceGrader(new BenchmarkGrade { Passed = false, Detail = "would-fail" });
        var service = ServiceWith(ledger, new StopWithAcceptanceDecider("npm", "test"), grader);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", null, GoalConfigWithRepo(), CancellationToken.None);

        result.AcceptancePassed.ShouldBe(false, "C2: the multi-repo stop grades the per-repo heads — a failing check is not accepted");
        grader.CallCount.ShouldBe(1, "the model check is graded per repo; the first repo fails → short-circuit");
        result.RepositoryBranches.ShouldBeEmpty("one failing repo withholds EVERY per-repo head — all-or-nothing");
    }

    [Fact]
    public async Task A_no_progress_forced_stop_is_never_graded_even_with_a_final_branch_and_repo()
    {
        // A FORCED stop (no-progress/governance) carries only {"reason":...} — no model acceptance — so it is never graded,
        // EVEN when the run has a final reviewable branch + repo (so the skip is the forced-stop shape, not no-branch).
        var ledger = SeedRunWithCleanMerge();   // Sequence 1: a clean merge → a final branch exists

        for (var i = 0; i < SupervisorLane.DefaultMaxNoProgressDecisions; i++)
            ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Plan, $$"""{"turn":{{i}}}""", "{}");   // trip the no-progress guard

        var grader = new FakeAcceptanceGrader(new BenchmarkGrade { Passed = true, Detail = "would-pass" });
        var service = ServiceWith(ledger, new AlwaysPlanDecider(), grader);   // the decider is never asked — the no-progress guard forces the stop

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", null, GoalConfigWithRepo(), CancellationToken.None);

        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Stop);
        result.TerminalReason.ShouldBe(SupervisorStopReasons.NoProgress, "the no-progress guard forced the stop");
        grader.CallCount.ShouldBe(0, "a forced stop has no model acceptance command → it is never graded");
        result.AcceptancePassed.ShouldBeNull("a forced stop reports Completed, never AcceptanceFailed");
    }

    [Fact]
    public async Task An_in_flight_stop_with_a_model_acceptance_is_graded_once_on_crash_recovery()
    {
        // A stop that crashed AFTER claim but BEFORE RecordTerminal is re-entered as context.InFlight → ReplayInFlightTurnAsync
        // re-executes + re-grades it (bounded, idempotent-by-design). Pins that the recovery walk DOES grade + fold the verdict
        // (the grade is on the execute path, which the in-flight replay re-runs — not the rehydrate fold).
        var ledger = SeedRunWithCleanMerge();   // Sequence 1: the final branch
        var stopPayload = JsonSerializer.Serialize(new SupervisorStopPayload { Outcome = "completed", Summary = "done", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "npm", "test" } } }, AgentJson.Options);
        ledger.SeedPending(_runId, _teamId, SupervisorDecisionKinds.Stop, stopPayload);   // Sequence 2: the crashed-mid-execution stop

        var grader = new FakeAcceptanceGrader(new BenchmarkGrade { Passed = false, Detail = "model-check-failed" });
        var service = ServiceWith(ledger, new AlwaysPlanDecider(), grader);   // the decider is NOT consulted on the in-flight replay path

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", null, GoalConfigWithRepo(), CancellationToken.None);

        result.IsFinished.ShouldBeTrue("the frozen in-flight stop is finished on recovery");
        result.AcceptancePassed.ShouldBe(false, "the recovered stop's model DoD is graded — it failed");
        result.IntegratedBranch.ShouldBeNull("the failed DoD withholds the head on recovery too");
        grader.CallCount.ShouldBe(1, "the recovery walk grades the in-flight stop's model command once");
        SupervisorOutcome.ReadAcceptanceGradePassed(StopRowOutcome(ledger)).ShouldBe(false, "the verdict is folded onto the recovered stop row");
    }

    // ── Replay: a re-run of a settled turn does NOT re-execute the side effect ───────

    [Fact]
    public async Task Replaying_a_settled_turn_does_not_double_execute()
    {
        var ledger = new FakeSupervisorDecisionLog();
        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, new StubSupervisorDecider(), executor, db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);

        // First pass: turn 0 (plan) executes once + records terminal.
        await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);
        executor.Calls.ShouldBe(1);
        ledger.Rows.Count.ShouldBe(1);

        // SIMULATE A REPLAY of turn 0 at the SAME turn number — re-derive the same per-turn key. The unique
        // index dedups to the prior terminal row (Duplicate) → the side effect is NOT re-run (no double-plan).
        var replayContext = new SupervisorTurnContext { Goal = "goal", TurnNumber = 0 };
        var decision = await new StubSupervisorDecider().DecideAsync(replayContext, CancellationToken.None);
        var key = SupervisorDecisionLog.DeriveIdempotencyKey(decision.Kind, decision.PayloadJson, SupervisorTurnService.TurnDiscriminator(0));
        var claim = await ledger.TryClaimAsync(_runId, _teamId, decision.Kind, key, "h", decision.PayloadJson, 0, CancellationToken.None);

        claim.Outcome.ShouldBe(SupervisorDecisionClaimOutcome.Duplicate, "the same per-turn key collides → replay the prior outcome");
        executor.Calls.ShouldBe(1, "the side effect ran exactly once — no double-plan on replay");
        ledger.Rows.Count.ShouldBe(1, "still exactly one row");
    }

    // ── P1-1 CROWN JEWEL: a crashed in-flight decision REPLAYS FROZEN, independent of decider determinism ──

    [Fact]
    public async Task Replaying_an_in_flight_turn_re_executes_the_frozen_decision_even_when_the_decider_is_non_deterministic()
    {
        // Simulate a crash mid-execution: turn 0's decision A (a plan) was claimed (INSERTed Pending) by a prior
        // walk that crashed BEFORE recording terminal — so the ledger holds ONE non-terminal A row. On re-entry
        // RehydrateFromDecisionLog folds it into context.InFlight; the fix replays A FROZEN (decider + bounds NOT
        // re-run) UNDER A's existing claim id (win the still-Pending begin-CAS → execute once → record terminal).
        // The decider here is NON-DETERMINISTIC — it would emit a DIFFERENT decision B on this turn. WITHOUT the
        // fix RunTurnAsync would ask the decider, get B, derive B's DIFFERENT key, find no match, INSERT a 2nd row,
        // execute B, and STRAND the A row forever. WITH the fix the decider is never consulted on replay, so its B
        // output is irrelevant and the ledger stays a single A row that the recovery finishes.
        var ledger = new FakeSupervisorDecisionLog();
        var plannedA = """{"subtasks":["a"]}""";
        ledger.SeedPending(_runId, _teamId, SupervisorDecisionKinds.Plan, plannedA);
        var inFlightId = ledger.Rows[0].Id;

        var decider = new NonDeterministicDecider(SupervisorDecisionKinds.Plan, plannedA, SupervisorDecisionKinds.Stop, """{"reason":"divergent-B"}""");
        var executor = new CountingExecutor();
        var service = new SupervisorTurnService(ledger, decider, executor, db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var result = await service.RunTurnAsync(_runId, _teamId, "sup", "goal", conversationId: null, goalConfig: null, CancellationToken.None);

        result.DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan, "the REPLAYED decision is the frozen in-flight A (plan), NOT the decider's divergent B (stop)");
        result.IsFinished.ShouldBeFalse("A is a plan → the turn self-advances, NOT B's stop");

        decider.CallCount.ShouldBe(0, "the decider was NOT consulted to produce the replayed decision — replay is frozen, independent of decider determinism");
        executor.Calls.ShouldBe(1, "the frozen A side effect re-executed exactly once on recovery");

        ledger.Rows.Count.ShouldBe(1, "EXACTLY ONE row for the turn — no divergent B row, no stranded A (without the fix the decider's B would derive a different key → a 2nd row + a strand)");
        ledger.Rows[0].Id.ShouldBe(inFlightId, "the single row is the SAME in-flight A row, re-executed under its existing claim");
        ledger.Rows[0].DecisionKind.ShouldBe(SupervisorDecisionKinds.Plan);
        ledger.Rows[0].PayloadJson.ShouldBe(plannedA, "the single row is A's frozen payload");
        ledger.Rows[0].Status.ShouldBe(SupervisorDecisionStatus.Succeeded, "the crashed A row reached terminal on recovery — no strand");
    }

    private SupervisorTurnService Service(FakeSupervisorDecisionLog ledger) =>
        new(ledger, new StubSupervisorDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);

    // ── L4 P1 stop-acceptance test helpers ──────────────────────────────────────────

    private SupervisorTurnService ServiceWith(FakeSupervisorDecisionLog ledger, ISupervisorDecider decider, FakeAcceptanceGrader grader) =>
        new(ledger, decider, new StubSupervisorActionExecutor(), db: null!, grader, new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);

    private static SupervisorDecision StopDecision() => new() { Kind = SupervisorDecisionKinds.Stop, PayloadJson = "{}" };

    private static SupervisorGoalConfig GoalConfigWithRepo() => new() { AgentProfile = new SupervisorAgentProfile { RepositoryId = Guid.NewGuid() } };

    private static string VerifiedResolveOutcome()
    {
        var resolver = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Succeeded", Summary = $"done {SupervisorResolverRecipe.TestsPassedMarker}", ProducedBranch = "codespace/resolve/final" };

        return JsonSerializer.Serialize(new { agentRunIds = new[] { resolver.AgentRunId }, agentCount = 1, agentResults = new[] { resolver } }, AgentJson.Options);
    }

    private static SupervisorTurnContext StopContextWithFinalBranch() => new()
    {
        Goal = "g",
        TurnNumber = 2,
        PriorDecisions = new[]
        {
            new SupervisorPriorDecision { Id = Guid.NewGuid(), Sequence = 1, DecisionKind = SupervisorDecisionKinds.Resolve, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = VerifiedResolveOutcome() },
        },
    };

    // A clean single-repo MERGE (not a resolve) is the run's final reviewable head here: merge is NOT a StagesAgents
    // decision, so rehydrate never reads the agent-results DB (null in this unit harness) — the full-turn tests stay DB-free.
    private FakeSupervisorDecisionLog SeedRunWithCleanMerge()
    {
        var ledger = new FakeSupervisorDecisionLog();
        var outcome = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch = "codespace/integration/final" } }, AgentJson.Options);

        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Merge, "{}", outcome);
        return ledger;
    }

    private FakeSupervisorDecisionLog SeedRunWithCleanMultiRepoMerge()
    {
        var ledger = new FakeSupervisorDecisionLog();
        var outcome = JsonSerializer.Serialize(new
        {
            integration = new
            {
                status = "Clean",
                repositories = new[]
                {
                    new { repositoryId = Guid.NewGuid(), alias = "web", status = "Clean", integratedBranch = "codespace/integration/run/turn1" },
                    new { repositoryId = Guid.NewGuid(), alias = "api", status = "Clean", integratedBranch = "codespace/integration/run/turn1" },
                },
            },
        }, AgentJson.Options);

        ledger.SeedTerminal(_runId, _teamId, SupervisorDecisionKinds.Merge, "{}", outcome);
        return ledger;
    }

    private static string? StopRowOutcome(FakeSupervisorDecisionLog ledger) => ledger.Rows.Last(r => r.DecisionKind == SupervisorDecisionKinds.Stop).OutcomeJson;

    /// <summary>A decider that STOPS with an optional model-authored acceptance command (empty args ⇒ no acceptance) — drives the stop-grade path.</summary>
    private sealed class StopWithAcceptanceDecider : ISupervisorDecider
    {
        private readonly string[] _command;

        public StopWithAcceptanceDecider(params string[] command) => _command = command;

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload
                {
                    Outcome = "completed",
                    Summary = "done",
                    Acceptance = _command.Length > 0 ? new SupervisorAcceptanceSpec { Command = _command } : null,
                }, AgentJson.Options),
            });
    }

    /// <summary>A decider that emits decision A on its FIRST call and a DIFFERENT decision B on every later one — models a non-deterministic real LLM re-asked on the same turn after a crash. The crown-jewel test asserts the replay ignores it entirely (CallCount stays 0).</summary>
    private sealed class NonDeterministicDecider : ISupervisorDecider
    {
        private readonly SupervisorDecision _first;
        private readonly SupervisorDecision _later;

        public NonDeterministicDecider(string firstKind, string firstPayload, string laterKind, string laterPayload)
        {
            _first = new SupervisorDecision { Kind = firstKind, PayloadJson = firstPayload };
            _later = new SupervisorDecision { Kind = laterKind, PayloadJson = laterPayload };
        }

        public int CallCount { get; private set; }

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(CallCount == 1 ? _first : _later);
        }
    }

    /// <summary>A decider that always plans — used to prove the budget (not the decider) is what terminates a runaway loop.</summary>
    // ── S3 plan-confirmation gate: inject / release / degrade wiring over the pure detection ──

    [Fact]
    public async Task An_unconfirmed_plan_preempts_the_decider_with_the_confirmation_ask_and_flips_the_status()
    {
        var row = PlanRow(CodeSpace.Messages.Plans.WorkPlanStatuses.Authored);
        var store = new FakeWorkPlanStore(row);
        // AlwaysPlanDecider would PLAN — the ask_human coming back proves the gate preempted the brain entirely.
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new AlwaysPlanDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), store, null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var decision = await service.ChooseDecisionAsync(GateContext(TerminalPlan()), SupervisorGoalPlan.From(null), depth: 0, CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.AskHuman, "no agent can be created before the operator confirms");

        // Parse rather than substring-match the raw payload — the JSON encoder escapes the marker's apostrophes.
        var question = System.Text.Json.JsonDocument.Parse(decision.PayloadJson).RootElement.GetProperty("question").GetString()!;
        question.ShouldContain(SupervisorPlanConfirmation.ConfirmationMarker);
        question.ShouldContain("plan v1");
        store.StatusFlips.ShouldBe(new[] { (CodeSpace.Messages.Plans.WorkPlanStatuses.Authored, CodeSpace.Messages.Plans.WorkPlanStatuses.AwaitingConfirmation) });
    }

    [Theory]
    [InlineData("approve", "Confirmed")]
    [InlineData("revise: merge the steps", "Rejected")]
    public async Task A_just_answered_confirmation_releases_to_the_decider_and_settles_the_status(string answer, string expectedStatus)
    {
        var row = PlanRow(CodeSpace.Messages.Plans.WorkPlanStatuses.AwaitingConfirmation);
        var store = new FakeWorkPlanStore(row);
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new AlwaysPlanDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), store, null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var decision = await service.ChooseDecisionAsync(GateContext(TerminalPlan(), AnsweredConfirmation(answer)), SupervisorGoalPlan.From(null), depth: 0, CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan, "the gate released — the decider's own decision proceeds with the folded answer");
        row.Status.ShouldBe(expectedStatus);
    }

    [Fact]
    public async Task No_conversation_surface_force_stops_rather_than_degrading_the_gate_open()
    {
        // A task launch wires no conversation — the injected card would DEGRADE to a no-surface self-advance and
        // agents would spawn unconfirmed (the review's blocker). The gate must stop the run instead.
        var store = new FakeWorkPlanStore(PlanRow(CodeSpace.Messages.Plans.WorkPlanStatuses.Authored));
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new AlwaysPlanDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), store, null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var decision = await service.ChooseDecisionAsync(GateContext(conversationId: null, TerminalPlan()), SupervisorGoalPlan.From(null), depth: 0, CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop, "no surface to ask on → fail-closed stop, never a silent bypass");
        decision.PayloadJson.ShouldContain(SupervisorStopReasons.PlanConfirmationUnavailable);
        store.StatusFlips.ShouldBeEmpty("no confirmation was ever requested — the plan honestly stays Authored");
    }

    [Theory]
    [InlineData(SupervisorDecisionKinds.Spawn)]
    [InlineData(SupervisorDecisionKinds.Retry)]
    public void A_spawn_while_the_latest_plan_stands_rejected_is_refused(string kind)
    {
        // Prompt-following is not a guarantee — the structural floor refuses to execute a REJECTED plan version.
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new StubSupervisorDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);
        var context = GateContext(TerminalPlan(), AnsweredConfirmation("revise: do not touch the DB"));

        var gated = service.ApplyPostDecisionGate(context, SupervisorGoalPlan.From(null), new SupervisorDecision { Kind = kind, PayloadJson = """{"subtaskIds":["sa","sb"]}""" });

        gated.Kind.ShouldBe(SupervisorDecisionKinds.Stop);
        gated.PayloadJson.ShouldContain(SupervisorStopReasons.RejectedPlanSpawnRefused);
    }

    [Fact]
    public void A_revised_plan_clears_the_rejected_floor()
    {
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new StubSupervisorDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), new FakeWorkPlanStore(), null!, null!, NullLogger<SupervisorTurnService>.Instance);
        var context = GateContext(TerminalPlan(), AnsweredConfirmation("revise: merge"), TerminalPlan());

        var spawn = new SupervisorDecision { Kind = SupervisorDecisionKinds.Spawn, PayloadJson = """{"subtaskIds":["sa"]}""" };

        service.ApplyPostDecisionGate(context, SupervisorGoalPlan.From(null), spawn)
            .Kind.ShouldBe(SupervisorDecisionKinds.Spawn, "authoring the revision clears the rejected floor by construction");
    }

    [Fact]
    public async Task The_release_flip_lands_even_when_a_pre_bound_stop_takes_the_turn()
    {
        // The approve arrives on a turn where a pre-decision bound (no-progress) force-stops: the stop must not
        // strand AwaitingConfirmation — the answered confirmation must settle FIRST.
        var row = PlanRow(CodeSpace.Messages.Plans.WorkPlanStatuses.AwaitingConfirmation);
        var store = new FakeWorkPlanStore(row);
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new AlwaysPlanDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), store, null!, null!, NullLogger<SupervisorTurnService>.Instance);

        // NoProgressDecisions at the cap → the pre-decision no-progress guard force-stops this turn.
        var context = GateContext(TerminalPlan(), AnsweredConfirmation("approve")) with { NoProgressDecisions = SupervisorLane.DefaultMaxNoProgressDecisions };

        var decision = await service.ChooseDecisionAsync(context, SupervisorGoalPlan.From(null), depth: 0, CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Stop, "the no-progress bound still stops the run");
        row.Status.ShouldBe(CodeSpace.Messages.Plans.WorkPlanStatuses.Confirmed, "but the answered confirmation settled FIRST — the persisted status never lies");
    }

    [Fact]
    public async Task A_missing_plan_row_degrades_open_rather_than_parking_on_an_unreviewable_card()
    {
        var store = new FakeWorkPlanStore();   // no row — unreachable in production (persist precedes the terminal plan)
        var service = new SupervisorTurnService(new FakeSupervisorDecisionLog(), new AlwaysPlanDecider(), new StubSupervisorActionExecutor(), db: null!, new FakeAcceptanceGrader(), new FakeDecisionQueue(), new FakeDecisionArbiter(), new FakeDecisionAnswerService(), store, null!, null!, NullLogger<SupervisorTurnService>.Instance);

        var decision = await service.ChooseDecisionAsync(GateContext(TerminalPlan()), SupervisorGoalPlan.From(null), depth: 0, CancellationToken.None);

        decision.Kind.ShouldBe(SupervisorDecisionKinds.Plan, "no checklist to review → the gate degrades open (logged), never a dead park");
        store.StatusFlips.ShouldBeEmpty();
    }

    private SupervisorTurnContext GateContext(params SupervisorPriorDecision[] priors) => GateContext(Guid.NewGuid(), priors);

    private SupervisorTurnContext GateContext(Guid? conversationId, params SupervisorPriorDecision[] priors) => new()
    {
        Goal = "goal",
        SupervisorRunId = _runId,
        TeamId = _teamId,
        NodeId = "sup",
        TurnNumber = priors.Length,
        PriorDecisions = priors,
        RequirePlanConfirmation = true,
        ConversationId = conversationId,
    };

    private CodeSpace.Core.Persistence.Entities.WorkPlan PlanRow(string status) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = _teamId,
        WorkflowRunId = _runId,
        Version = 1,
        Status = status,
        OriginKind = "agent.supervisor",
        Goal = "goal",
        ItemsJson = """[{"id":"sa"},{"id":"sb"}]""",
    };

    private static SupervisorPriorDecision TerminalPlan() => new()
    {
        Id = Guid.NewGuid(),
        Sequence = 0,
        DecisionKind = SupervisorDecisionKinds.Plan,
        Status = SupervisorDecisionStatus.Succeeded,
        PayloadJson = """{"goal":"g","subtasks":[]}""",
        OutcomeJson = "{}",
    };

    private static SupervisorPriorDecision AnsweredConfirmation(string answer)
    {
        var card = SupervisorPlanConfirmation.IntoAskHuman(1, 2);
        return new SupervisorPriorDecision
        {
            Id = Guid.NewGuid(),
            Sequence = 1,
            DecisionKind = card.Kind,
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = card.PayloadJson,
            OutcomeJson = System.Text.Json.JsonSerializer.Serialize(new { question = "q", askHumanToken = "tok", answer }),
        };
    }

    private sealed class AlwaysPlanDecider : ISupervisorDecider
    {
        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision { Kind = SupervisorDecisionKinds.Plan, PayloadJson = """{"x":1}""" });
    }

    /// <summary>Counts ExecuteAsync calls — proves the side effect runs exactly once (no double-execute on replay).</summary>
    private sealed class CountingExecutor : ISupervisorActionExecutor
    {
        public int Calls { get; private set; }

        public Task<SupervisorExecution> ExecuteAsync(SupervisorDecision decision, SupervisorTurnContext context, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(SupervisorExecution.Synchronous("{}"));
        }
    }
}

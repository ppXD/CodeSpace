using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.Core.Services.Workflows.Nodes.Builtin;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/>.<c>RehydrateFromDecisionLogAsync</c> with
/// a fake grader at the one seam): the A3 OBJECTIVE acceptance fold. Proves the replay-safety contract — the clone+grade
/// runs EXACTLY ONCE at the terminal resolve fold, the verdict is folded + persisted, and every later rehydrate reads
/// the folded verdict off the durable tape WITHOUT re-grading (the named biggest risk, defeated). Plus: a failed grade
/// OVERRIDES the resolver's self-report marker (Unverified → the resolved branch is withheld); a run with no operator
/// acceptance command is byte-identical (never grades); and a grader failure fails closed without stranding the run.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorAcceptanceFoldFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorAcceptanceFoldFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";
    private static readonly string Marker = SupervisorResolverRecipe.TestsPassedMarker;
    private static readonly string[] Command = { "sh", "check.sh" };

    [Fact]
    public async Task A_resolve_is_graded_once_and_the_objective_verdict_is_folded_and_persisted()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/x", markerPresent: true));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), grader);

        grader.CallCount.ShouldBe(1, "the grade runs exactly once at the fold");
        grader.LastCall!.Value.RepositoryId.ShouldBe(repoId);
        grader.LastCall.Value.TeamId.ShouldBe(teamId);
        grader.LastCall.Value.Branch.ShouldBe("codespace/resolve/x", "the resolver's produced branch is graded");
        grader.LastCall.Value.Command.ShouldBe(Command, "the operator's acceptance command is the graded argv");

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBe(true, "the objective verdict is folded into the in-memory outcome");
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Verified);

        SupervisorOutcome.ReadAcceptanceGradePassed(await LedgerOutcomeAsync(runId, teamId))
            .ShouldBe(true, "the grade is PERSISTED onto the durable ledger row (so replay reads it, not re-grades)");
    }

    [Fact]
    public async Task A_resolver_that_never_pushed_grades_via_its_own_recorded_patch_not_fail_closed()
    {
        // S2: forcePushBranch=true doesn't exempt a resolver from a repo-level PatchOnly policy (PR-2) — its push can
        // still be guard-blocked, leaving only a recorded patch. The grade must still run, not fail closed.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcomeWithAgentId(agentId, producedBranch: null, markerPresent: true));
        await SeedManifestAsync(teamId, agentId, repoId, branch: null, baseSha: "deadbeef", patchArtifactId: Guid.NewGuid());

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), grader);

        grader.CallCount.ShouldBe(0, "no branch → the branch-based path is never invoked");
        grader.PatchCallCount.ShouldBe(1, "the resolver's own recorded patch is graded instead of failing closed");

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBe(true);
    }

    [Fact]
    public async Task A_resolver_with_no_branch_and_no_patch_still_fails_closed_resolve_has_no_expects_changes_concept()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcomeWithAgentId(Guid.NewGuid(), producedBranch: null, markerPresent: true));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), grader);

        grader.CallCount.ShouldBe(0);
        grader.PatchCallCount.ShouldBe(0);

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBe(false, "a resolver always expects to reconcile SOMETHING — nothing to grade is a real failure, never vacuous");
    }

    [Fact]
    public async Task A_failed_grade_overrides_the_resolvers_self_report_and_withholds_the_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        // The resolver SUCCEEDED and self-reported the marker (the self-report WOULD accept) — but the objective grade FAILS.
        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/bad", markerPresent: true));

        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1" }));

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Unverified, "the objective grade overrides the self-report marker — the regression A3 closes");
        SupervisorOutcome.ResolvedBranch(resolve).ShouldBeNull("an Unverified resolve surfaces NO clean head → the accept short-circuit is withheld");
    }

    [Fact]
    public async Task Replay_reads_the_folded_verdict_without_re_grading()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/x", markerPresent: true));

        await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" }));

        var persistedAfterFirst = await LedgerOutcomeAsync(runId, teamId);

        // The SECOND rehydrate must NOT re-clone+grade — the once-guard reads the folded verdict off the tape.
        var secondGrader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "WOULD-FLIP-IF-RE-GRADED" });
        var ctx2 = await RehydrateAsync(runId, teamId, GoalConfig(repoId, Command), secondGrader);

        secondGrader.CallCount.ShouldBe(0, "replay reads the durable verdict — the grade I/O never re-runs (replay-deterministic)");
        ctx2.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve).OutcomeJson
            .ShouldBe(persistedAfterFirst, "the verdict is unchanged across replay — a pure tape read");
        (await LedgerOutcomeAsync(runId, teamId)).ShouldBe(persistedAfterFirst, "no redundant UPDATE on replay");
    }

    [Fact]
    public async Task A_run_with_no_acceptance_command_never_grades_and_does_not_rewrite_the_row()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/x", markerPresent: true));
        var before = await LedgerOutcomeAsync(runId, teamId);   // jsonb-normalized; compare row-to-row, not to the seeded compact bytes

        // No AcceptanceChecks configured → the marker self-report stands, no grade runs, no spurious write.
        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId: Guid.NewGuid(), acceptanceChecks: null), grader);

        grader.CallCount.ShouldBe(0, "no operator acceptance command → no objective grade runs");
        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBeNull("no acceptanceGrade field is folded");
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Verified, "the verdict falls back to the self-report marker");
        (await LedgerOutcomeAsync(runId, teamId)).ShouldBe(before, "the durable resolve row is unchanged — no spurious UPDATE on the no-command path");
    }

    [Fact]
    public async Task A_grader_failure_fails_closed_without_stranding_the_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "codespace/resolve/x", markerPresent: true));

        // An UNEXPECTED grader throw (not the fail-closed Failed grade A2 normally returns) must still not crash the
        // terminal fold (which would strand the row); it degrades to a not-accepted verdict.
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid(), Command), new RecordingGrader(new InvalidOperationException("sandbox exploded")));

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBe(false, "an unexpected grade failure folds not-accepted");
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Unverified);
    }

    [Fact]
    public async Task A_multi_repo_resolve_is_not_graded_and_falls_back_to_the_marker()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        // A resolver result carrying per-repo RepositoryResults = a MULTI-repo resolve. Its top-level ProducedBranch
        // mirrors only the primary, so A3 must NOT grade it (a primary-only check would gate every repo's branch — a
        // false accept if a secondary is broken). The per-repo grade is a deferred follow-up; until then, the marker stands.
        await SeedResolveDecisionAsync(runId, teamId, MultiRepoResolveOutcome());

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid(), Command), grader);

        grader.CallCount.ShouldBe(0, "a multi-repo resolve is not graded by A3 (per-repo grade deferred) — never a primary-only false accept");
        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadAcceptanceGradePassed(resolve.OutcomeJson).ShouldBeNull("no grade is folded onto a multi-repo resolve");
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Verified, "the marker verdict stands for a multi-repo resolve, byte-identical to pre-A3");
    }

    // ─── L4 P1: a terminal STOP's MODEL-authored acceptance is graded inline + persisted on the real ledger ───

    [Theory]
    [InlineData(true, "Completed")]        // the model definition-of-done passes → the run completes, branch surfaces
    [InlineData(false, "AcceptanceFailed")] // it fails → AcceptanceFailed, the reviewable branch is withheld
    public async Task A_terminal_stop_grades_the_model_command_and_persists_the_verdict(bool gradePassed, string expectedStatus)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        // A clean single-repo merge is the run's final reviewable head — the branch the stop's model check grades against.
        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var (result, grader) = await RunStopTurnAsync(runId, teamId, GoalConfig(repoId, acceptanceChecks: null), new[] { "sh", "check.sh" }, new BenchmarkGrade { Passed = gradePassed, Detail = "model-check" });

        grader.CallCount.ShouldBe(1, "the model command is graded once against the run's final branch");
        grader.LastCall!.Value.Branch.ShouldBe("codespace/integration/x");
        grader.LastCall.Value.Command.ShouldBe(new[] { "sh", "check.sh" }, "the model-authored command is the graded argv");

        result.AcceptancePassed.ShouldBe(gradePassed);
        result.IntegratedBranch.ShouldBe(gradePassed ? "codespace/integration/x" : null, "a failed model DoD withholds the reviewable head");
        AgentSupervisorNode.Finish(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, result).Outputs["status"].GetString().ShouldBe(expectedStatus);

        SupervisorOutcome.ReadAcceptanceGradePassed(await StopLedgerOutcomeAsync(runId, teamId))
            .ShouldBe(gradePassed, "the verdict is PERSISTED onto the durable stop row");
    }

    [Fact]
    public async Task P3_5_the_acceptance_grade_call_is_labeled_grader_acceptance_for_the_cost_cap_fold()
    {
        // P3.5 — the acceptance grader's OWN model call (an LlmJudge kind) must be recorded + counted toward the
        // cost cap exactly like the decider's "supervisor.decision" and the critic's "critic.review". Proves the
        // LlmCallContext.Push wrap around the grading seam is ACTUALLY active (not just present in the source) by
        // capturing the ambient scope from INSIDE a stub grader — the same seam a real LlmJudgeGrader model call rides.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var grader = new ScopeCapturingGrader(new BenchmarkGrade { Passed = true, Detail = "ok" });

        await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(Guid.NewGuid(), acceptanceChecks: null), new[] { "sh", "check.sh" }, grader);

        grader.CapturedKind.ShouldBe(SupervisorTurnService.GraderAcceptanceCallKind, "the grade call ran under the 'grader.acceptance' label — its spend is now recorded + countable toward the cost cap");
        grader.CapturedRunId.ShouldBe(runId);
        grader.CapturedTeamId.ShouldBe(teamId);
    }

    [Fact]
    public async Task A_terminal_stop_with_no_model_acceptance_never_grades_and_is_byte_identical()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var (result, grader) = await RunStopTurnAsync(runId, teamId, GoalConfig(Guid.NewGuid(), acceptanceChecks: null), command: Array.Empty<string>(), new BenchmarkGrade { Passed = false, Detail = "should-not-run" });

        grader.CallCount.ShouldBe(0, "a stop without a model acceptance command never grades");
        result.AcceptancePassed.ShouldBeNull();
        result.IntegratedBranch.ShouldBe("codespace/integration/x", "the branch surfaces as before");
        SupervisorOutcome.ReadAcceptanceGradePassed(await StopLedgerOutcomeAsync(runId, teamId)).ShouldBeNull("the durable stop row carries no acceptanceGrade — byte-identical to pre-slice");
    }

    // ─── L4 C1: the OPERATOR acceptance floor gates EVERY terminal stop, even a clean (no-conflict) run ───

    [Theory]
    [InlineData(true, "Completed")]
    [InlineData(false, "AcceptanceFailed")]
    public async Task A_clean_run_with_no_model_check_is_still_gated_by_the_operator_floor(bool floorPasses, string expectedStatus)
    {
        // The C1 gap: a clean run (merge, no conflict → no resolve) whose stop carries NO model definition-of-done.
        // Before C1 the operator's floor was enforced ONLY at resolve, so this run shipped ungated. Now the stop
        // grades the operator floor against the final head — the model can no longer bypass it by authoring no check.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var floor = new[] { "sh", "floor.sh" };
        var grader = new RecordingGrader(new BenchmarkGrade { Passed = floorPasses, Detail = "operator-floor" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(repoId, acceptanceChecks: floor), command: Array.Empty<string>(), grader);

        grader.CallCount.ShouldBe(1, "the operator floor is graded even with no model check authored");
        grader.LastCall!.Value.Command.ShouldBe(floor, "the OPERATOR's acceptance command is the graded argv");
        result.AcceptancePassed.ShouldBe(floorPasses);
        result.IntegratedBranch.ShouldBe(floorPasses ? "codespace/integration/x" : null, "a failed operator floor withholds the reviewable head");
        AgentSupervisorNode.Finish(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, result).Outputs["status"].GetString().ShouldBe(expectedStatus);
    }

    [Fact]
    public async Task The_operator_floor_is_graded_FIRST_and_short_circuits_the_model_check_on_failure()
    {
        // Precedence + AND: when BOTH gates are present and the operator floor FAILS, the model check is never graded
        // (the floor is the mandatory gate; the model can only tighten on top of it).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var floor = new[] { "sh", "floor.sh" };
        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "floor-failed" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(Guid.NewGuid(), acceptanceChecks: floor), command: new[] { "sh", "model.sh" }, grader);

        grader.CallCount.ShouldBe(1, "the operator floor failed → the model check is short-circuited, never graded");
        grader.LastCall!.Value.Command.ShouldBe(floor, "the operator floor is graded FIRST");
        result.AcceptancePassed.ShouldBe(false);
    }

    [Fact]
    public async Task A_passing_operator_floor_AND_a_failing_model_check_is_not_accepted()
    {
        // The AND across both gates: the operator floor passes but the model's own tightening fails → not accepted.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var floor = new[] { "sh", "floor.sh" };
        var model = new[] { "sh", "model.sh" };
        var grader = new QueuedGrader(new BenchmarkGrade { Passed = true, Detail = "floor-ok" }, new BenchmarkGrade { Passed = false, Detail = "model-failed" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(Guid.NewGuid(), acceptanceChecks: floor), command: model, grader);

        grader.CallCount.ShouldBe(2, "the operator floor passed → the model check is graded too (the AND)");
        grader.Commands[0].ShouldBe(floor, "operator floor first");
        grader.Commands[1].ShouldBe(model, "then the model's tightening");
        result.AcceptancePassed.ShouldBe(false, "both gates must pass — a failing model check is not accepted");
    }

    [Fact]
    public async Task The_model_authored_oracle_kind_gates_only_the_model_check_the_floor_stays_tests_pass()
    {
        // T1.1: the model authors a NON-coding deliverable check (ArtifactPresent) on ITS gate. The trust guard is that
        // the operator floor is ALWAYS graded TestsPass server-side (the model never authors the floor's oracle); only
        // the model's own tightening gate honors SupervisorAcceptanceSpec.Kind.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var floor = new[] { "sh", "floor.sh" };
        var modelPaths = new[] { "docs/report.md" };
        var grader = new QueuedGrader(new BenchmarkGrade { Passed = true, Detail = "floor-ok" }, new BenchmarkGrade { Passed = true, Detail = "artifacts-present" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(Guid.NewGuid(), acceptanceChecks: floor), command: modelPaths, grader, modelKind: BenchmarkGradingKind.ArtifactPresent);

        grader.CallCount.ShouldBe(2, "both gates graded — operator floor then the model's tightening");
        grader.Kinds[0].ShouldBe(BenchmarkGradingKind.TestsPass, "the operator floor is ALWAYS graded TestsPass — the model never authors the floor's oracle");
        grader.Kinds[1].ShouldBe(BenchmarkGradingKind.ArtifactPresent, "the model-check gate honors the model-authored oracle kind");
        result.AcceptancePassed.ShouldBe(true, "floor (TestsPass) AND model (ArtifactPresent) both pass → accepted");
        result.IntegratedBranch.ShouldBe("codespace/integration/x", "an accepted run surfaces its reviewable head");
    }

    [Fact]
    public async Task A_model_artifact_check_that_fails_withholds_the_head()
    {
        // The same non-coding stop, but the declared deliverable is missing → the model's ArtifactPresent gate fails →
        // not accepted → the reviewable head is withheld (the deterministic non-coding analogue of a failing test gate).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var grader = new QueuedGrader(new BenchmarkGrade { Passed = true, Detail = "floor-ok" }, new BenchmarkGrade { Passed = false, Detail = "artifact-missing: docs/report.md" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(Guid.NewGuid(), acceptanceChecks: new[] { "sh", "floor.sh" }), command: new[] { "docs/report.md" }, grader, modelKind: BenchmarkGradingKind.ArtifactPresent);

        grader.Kinds[1].ShouldBe(BenchmarkGradingKind.ArtifactPresent, "the missing-artifact verdict came from the ArtifactPresent oracle");
        result.AcceptancePassed.ShouldBe(false, "a missing deliverable is not accepted");
        result.IntegratedBranch.ShouldBeNull("a failed model artifact check withholds the reviewable head");
    }

    [Fact]
    public async Task Both_gates_passing_accepts_and_surfaces_the_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedCleanMergeDecisionAsync(runId, teamId, "codespace/integration/x");

        var grader = new QueuedGrader(new BenchmarkGrade { Passed = true, Detail = "floor-ok" }, new BenchmarkGrade { Passed = true, Detail = "model-ok" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(Guid.NewGuid(), acceptanceChecks: new[] { "sh", "floor.sh" }), command: new[] { "sh", "model.sh" }, grader);

        grader.CallCount.ShouldBe(2);
        result.AcceptancePassed.ShouldBe(true);
        result.IntegratedBranch.ShouldBe("codespace/integration/x", "both gates passed → the reviewable head surfaces");
    }

    // ─── L4 C2: a MULTI-repo stop grades EVERY per-repo head — all-or-nothing, fail-closed ───

    [Fact]
    public async Task A_multi_repo_stop_grades_every_repo_head_and_accepts_when_all_pass()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();

        await SeedMultiRepoMergeDecisionAsync(runId, teamId, (web, "web", "web/x"), (api, "api", "api/x"));

        var grader = new QueuedGrader(new BenchmarkGrade { Passed = true, Detail = "web-ok" }, new BenchmarkGrade { Passed = true, Detail = "api-ok" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(web, acceptanceChecks: new[] { "sh", "floor.sh" }), command: Array.Empty<string>(), grader);

        grader.CallCount.ShouldBe(2, "the operator floor is graded against EACH repo's head");
        result.AcceptancePassed.ShouldBe(true);
        result.RepositoryBranches.Count.ShouldBe(2, "all repos passed → every per-repo head surfaces");
    }

    [Fact]
    public async Task A_multi_repo_stop_is_all_or_nothing_one_failing_repo_withholds_every_branch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var web = Guid.NewGuid();
        var api = Guid.NewGuid();

        await SeedMultiRepoMergeDecisionAsync(runId, teamId, (web, "web", "web/x"), (api, "api", "api/x"));

        // web passes, api FAILS the operator floor → the WHOLE multi-repo change is not accepted (a partial ship of an
        // interdependent change is unsafe).
        var grader = new QueuedGrader(new BenchmarkGrade { Passed = true, Detail = "web-ok" }, new BenchmarkGrade { Passed = false, Detail = "api-tests-failed" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(web, acceptanceChecks: new[] { "sh", "floor.sh" }), command: Array.Empty<string>(), grader);

        grader.CallCount.ShouldBe(2, "both repos are graded; api fails the second");
        result.AcceptancePassed.ShouldBe(false);
        result.RepositoryBranches.ShouldBeEmpty("one failing repo withholds EVERY per-repo head — all-or-nothing");
    }

    [Fact]
    public async Task A_multi_repo_head_with_no_repository_id_fails_closed_never_silently_passing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var web = Guid.NewGuid();

        // A per-repo head missing its repository id can't be cloned → it must fail closed, never slip past ungraded —
        // even though the gradeable repo passes.
        await SeedMultiRepoMergeDecisionAsync(runId, teamId, (web, "web", "web/x"), (null, "ghost", "ghost/x"));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "ok" });
        var result = await RunStopTurnWithGraderAsync(runId, teamId, GoalConfig(web, acceptanceChecks: new[] { "sh", "floor.sh" }), command: Array.Empty<string>(), grader);

        result.AcceptancePassed.ShouldBe(false, "an un-gradeable repo (no id) fails the whole stop closed");
        result.RepositoryBranches.ShouldBeEmpty();
    }

    // ─── Crown jewel: the REAL grader (DI-resolved) wired through the real fold against a real repo + branch ───

    [Theory]
    [InlineData(0, "Verified")]     // the resolver's branch genuinely passes the operator check → objectively accepted
    [InlineData(1, "Unverified")]   // it FAILS the check → the objective grade overrides the self-report marker, end-to-end
    public async Task The_real_grade_drives_the_verdict_over_a_real_repo_branch(int checkExitCode, string expectedVerdict)
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithCheckAsync("resolve/head", checkExitCode);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        // The resolver SUCCEEDED + self-reported the marker (it would accept), with the produced branch on the remote.
        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcome(producedBranch: "resolve/head", markerPresent: true));

        // Resolve the REAL SupervisorTurnService (real SupervisorAcceptanceGrader) — it clones repoId@resolve/head and
        // runs the operator command for real; no fake at any seam.
        SupervisorTurnContext ctx;
        using (var scope = _fixture.BeginScope())
            ctx = await scope.Resolve<ISupervisorTurnService>()
                .RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, GoalConfig(repoId, Command), CancellationToken.None);

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ToString()
            .ShouldBe(expectedVerdict, "the REAL acceptance check's exit code drives the verdict, overriding the resolver's self-report");
        if (expectedVerdict == "Unverified")
            SupervisorOutcome.ResolvedBranch(resolve).ShouldBeNull("a really-failing check withholds the accept boundary");
        else
            SupervisorOutcome.ResolvedBranch(resolve).ShouldBe("resolve/head", "a really-passing check accepts the resolved branch");
    }

    [Fact]
    public async Task The_real_grade_applies_a_real_recorded_patch_onto_a_real_base_when_the_resolver_never_pushed()
    {
        // S2 crown jewel: NO fake at ANY seam — real Postgres, real git clone, a REAL offloaded artifact (the same
        // IArtifactOffloader a genuine producer uses), and the REAL SupervisorAcceptanceGrader.GradePatchAsync
        // applying the recorded diff onto a FRESH independent clone. check.sh only exits 0 if marker.txt exists —
        // it can ONLY pass if the patch was genuinely applied, proving the whole S2 mechanism end to end.
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        var baseSha = await remote.SeedMarkerCheckAsync("marker.txt");
        // Padded well over the 8KB inline-offload threshold, so IArtifactOffloader genuinely stores this as an
        // artifact (a small diff would stay inline and never exercise the SAME offload path a real producer uses).
        var patch = await remote.MakePatchAsync(baseSha, dir => File.WriteAllText(Path.Combine(dir, "marker.txt"), new string('m', 9000)));

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);
        var agentId = Guid.NewGuid();

        // Offload the patch through the REAL team-scoped artifact store — the same primitive a genuine producer's
        // capture uses — so PatchArtifactId resolves to real content, never a hand-faked reference.
        Guid artifactId;
        using (var scope = _fixture.BeginScope())
        {
            var offloaded = await scope.Resolve<Core.Services.Workflows.Artifacts.IArtifactOffloader>()
                .OffloadIfLargeAsync(teamId, patch, "text/x-diff", CancellationToken.None);
            artifactId = offloaded.ArtifactId ?? throw new InvalidOperationException("expected the offloader to actually store this artifact for the resolve step below");
        }

        await SeedResolveDecisionAsync(runId, teamId, ResolveOutcomeWithAgentId(agentId, producedBranch: null, markerPresent: true));
        await SeedManifestAsync(teamId, agentId, repoId, branch: null, baseSha: baseSha, patchArtifactId: artifactId);

        // Resolve the REAL SupervisorTurnService (real SupervisorAcceptanceGrader) — no fake at any seam.
        SupervisorTurnContext ctx;
        using (var scope = _fixture.BeginScope())
            ctx = await scope.Resolve<ISupervisorTurnService>()
                .RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, GoalConfig(repoId, Command), CancellationToken.None);

        var resolve = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Resolve);
        SupervisorOutcome.ReadResolutionVerdict(resolve.OutcomeJson).ShouldBe(SupervisorResolutionVerdict.Verified,
            "the real grader really cloned the base, really applied the recorded patch, and check.sh really found marker.txt — the whole S2 mechanism, no fakes");
    }

    [Theory]
    [InlineData(0, "Completed")]            // the model definition-of-done really passes → the run completes
    [InlineData(1, "AcceptanceFailed")]     // it really fails → AcceptanceFailed end-to-end, the head is withheld
    public async Task The_real_grade_drives_the_terminal_stop_status_over_a_real_repo_branch(int checkExitCode, string expectedStatus)
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithCheckAsync("integration/head", checkExitCode);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        // The run's final reviewable head is a REAL branch on the remote; the stop's model check clones + runs it for real.
        await SeedCleanMergeDecisionAsync(runId, teamId, "integration/head");

        SupervisorTurnResult result;
        using (var scope = _fixture.BeginScope())
        {
            // The REAL SupervisorAcceptanceGrader at the grade seam (clones integration/head + runs check.sh); a fixed
            // stop-authoring decider stands in for the LLM brain so no model is needed.
            var service = new SupervisorTurnService(
                scope.Resolve<ISupervisorDecisionLog>(),
                new StopWithAcceptanceDecider(new[] { "sh", "check.sh" }),
                scope.Resolve<ISupervisorActionExecutor>(),
                scope.Resolve<CodeSpaceDbContext>(),
                scope.Resolve<ISupervisorAcceptanceGrader>(),
                scope.Resolve<IDecisionQueueService>(),
                scope.Resolve<IDecisionArbiter>(),
                scope.Resolve<IDecisionAnswerService>(),
                scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
                scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(), scope.Resolve<ILogger<SupervisorTurnService>>());

            result = await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, GoalConfig(repoId, acceptanceChecks: null), CancellationToken.None);
        }

        AgentSupervisorNode.Finish(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, result).Outputs["status"].GetString()
            .ShouldBe(expectedStatus, "the REAL acceptance check's exit code drives the terminal stop status end-to-end");
        result.IntegratedBranch.ShouldBe(checkExitCode == 0 ? "integration/head" : null, "a really-failing model DoD withholds the reviewable head");
    }

    [Theory]
    [InlineData(0, "Completed")]            // the operator floor really passes on a clean run → the run completes
    [InlineData(1, "AcceptanceFailed")]     // it really fails → AcceptanceFailed end-to-end, the head is withheld
    public async Task The_real_operator_floor_gates_a_clean_runs_terminal_stop_over_a_real_repo_branch(int checkExitCode, string expectedStatus)
    {
        if (!await GitReadyAsync()) return;

        using var remote = new AcceptanceRemote();
        await remote.InitAsync();
        await remote.AddBranchWithCheckAsync("integration/head", checkExitCode);

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = await SeedBoundRepositoryAsync(teamId, remote.Url);

        // A clean (no-conflict) run with NO model definition-of-done — the C1 gap. The OPERATOR floor (acceptanceChecks)
        // must still clone integration/head + run check.sh for real and gate the terminal.
        await SeedCleanMergeDecisionAsync(runId, teamId, "integration/head");

        SupervisorTurnResult result;
        using (var scope = _fixture.BeginScope())
        {
            // The REAL grader at the seam; the stop authors NO model check (empty command) so ONLY the operator floor runs.
            var service = new SupervisorTurnService(
                scope.Resolve<ISupervisorDecisionLog>(),
                new StopWithAcceptanceDecider(Array.Empty<string>()),
                scope.Resolve<ISupervisorActionExecutor>(),
                scope.Resolve<CodeSpaceDbContext>(),
                scope.Resolve<ISupervisorAcceptanceGrader>(),
                scope.Resolve<IDecisionQueueService>(),
                scope.Resolve<IDecisionArbiter>(),
                scope.Resolve<IDecisionAnswerService>(),
                scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
                scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(), scope.Resolve<ILogger<SupervisorTurnService>>());

            result = await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, GoalConfig(repoId, acceptanceChecks: new[] { "sh", "check.sh" }), CancellationToken.None);
        }

        AgentSupervisorNode.Finish(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, result).Outputs["status"].GetString()
            .ShouldBe(expectedStatus, "the REAL operator-floor check's exit code gates the clean run's terminal stop end-to-end");
        result.IntegratedBranch.ShouldBe(checkExitCode == 0 ? "integration/head" : null, "a really-failing operator floor withholds the reviewable head even with no model check");
    }

    [Theory]
    [InlineData(0, "Completed")]            // both repos' checks really pass → the multi-repo run completes
    [InlineData(1, "AcceptanceFailed")]     // the SECOND repo really fails → the WHOLE multi-repo change is withheld
    public async Task The_real_operator_floor_gates_a_multi_repo_stop_all_or_nothing_over_real_repo_branches(int secondRepoExit, string expectedStatus)
    {
        if (!await GitReadyAsync()) return;

        using var remoteA = new AcceptanceRemote();
        await remoteA.InitAsync();
        await remoteA.AddBranchWithCheckAsync("head", 0);          // repo A always passes

        using var remoteB = new AcceptanceRemote();
        await remoteB.InitAsync();
        await remoteB.AddBranchWithCheckAsync("head", secondRepoExit);   // repo B is the swing vote

        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoA = await SeedBoundRepositoryAsync(teamId, remoteA.Url);
        var repoB = await SeedBoundRepositoryAsync(teamId, remoteB.Url);

        await SeedMultiRepoMergeDecisionAsync(runId, teamId, (repoA, "a", "head"), (repoB, "b", "head"));

        SupervisorTurnResult result;
        using (var scope = _fixture.BeginScope())
        {
            // The REAL grader clones EACH repo@head + runs check.sh for real; the stop authors no model check so only
            // the operator floor runs, against BOTH repos.
            var service = new SupervisorTurnService(
                scope.Resolve<ISupervisorDecisionLog>(),
                new StopWithAcceptanceDecider(Array.Empty<string>()),
                scope.Resolve<ISupervisorActionExecutor>(),
                scope.Resolve<CodeSpaceDbContext>(),
                scope.Resolve<ISupervisorAcceptanceGrader>(),
                scope.Resolve<IDecisionQueueService>(),
                scope.Resolve<IDecisionArbiter>(),
                scope.Resolve<IDecisionAnswerService>(),
                scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
                scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(), scope.Resolve<ILogger<SupervisorTurnService>>());

            result = await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, GoalConfig(repoA, acceptanceChecks: new[] { "sh", "check.sh" }), CancellationToken.None);
        }

        AgentSupervisorNode.Finish(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, result).Outputs["status"].GetString()
            .ShouldBe(expectedStatus, "every repo's REAL check must pass; one failing repo fails the whole multi-repo stop");
        result.RepositoryBranches.Count.ShouldBe(secondRepoExit == 0 ? 2 : 0, "all-or-nothing — one failing repo withholds EVERY per-repo head");
    }

    // ─── Helpers ───

    private async Task<Guid> SeedBoundRepositoryAsync(Guid teamId, string cloneUrlHttps)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var instanceId = Guid.NewGuid();
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = Messages.Enums.ProviderKind.GitHub, DisplayName = "local", BaseUrl = $"https://local/{instanceId:N}" });

        var serializer = scope.Resolve<Core.Services.Credentials.ICredentialPayloadSerializer>();
        var encryptor = scope.Resolve<Core.Services.Credentials.IPayloadEncryptor>();
        var payloadJson = serializer.Serialize(new Messages.Credentials.PatPayload { Token = "integration-token" });

        var credentialId = Guid.NewGuid();
        db.Credential.Add(new Credential
        {
            Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = Messages.Enums.AuthType.Pat, DisplayName = "clone cred",
            EncryptedPayload = encryptor.Encrypt(payloadJson), Status = Messages.Enums.CredentialStatus.Active,
        });

        var repoId = Guid.NewGuid();
        db.Repository.Add(new Repository
        {
            Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId,
            ExternalId = repoId.ToString(), NamespacePath = "org", Name = "repo", FullPath = "org/repo",
            DefaultBranch = "main", CloneUrlHttps = cloneUrlHttps, WebUrl = "https://local/org/repo",
        });

        await db.SaveChangesAsync();
        return repoId;
    }

    private static async Task<bool> GitReadyAsync()
    {
        if (OperatingSystem.IsWindows()) return false;
        try { return (await new Core.Services.Agents.Sandbox.Runners.LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = new[] { "--version" }, TimeoutSeconds = 10 }, CancellationToken.None)).Status == SandboxStatus.Success; }
        catch { return false; }
    }

    /// <summary>A bare file:// remote with a main commit + acceptance branches each carrying a check.sh whose exit code is the start-state — the real branch the real grader clones + checks.</summary>
    private sealed class AcceptanceRemote : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "cs-sup-a3-" + Guid.NewGuid().ToString("N"));
        private readonly string _bare;
        private readonly string _seed;

        public AcceptanceRemote()
        {
            Directory.CreateDirectory(_root);
            _bare = Path.Combine(_root, "remote.git");
            _seed = Path.Combine(_root, "seed");
        }

        public string Url => new Uri(_bare).AbsoluteUri;

        public async Task InitAsync()
        {
            await Git(_root, "init", "--bare", "-b", "main", _bare);
            Directory.CreateDirectory(_seed);
            await Git(_seed, "clone", _bare, _seed);
            await Config(_seed);
            await File.WriteAllTextAsync(Path.Combine(_seed, "README.md"), "base\n");
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", "seed");
            await Git(_seed, "push", "origin", "main");
        }

        public async Task AddBranchWithCheckAsync(string branch, int checkExitCode)
        {
            await Git(_seed, "checkout", "-B", branch, "main");
            await File.WriteAllTextAsync(Path.Combine(_seed, "check.sh"), $"#!/bin/sh\nexit {checkExitCode}\n");
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", $"check exit {checkExitCode}");
            await Git(_seed, "push", "origin", branch);
            await Git(_seed, "checkout", "main");
        }

        /// <summary>Push a check.sh onto <c>main</c> whose exit code depends on whether <paramref name="markerFile"/> exists in the working tree — the S2 real-patch proof's oracle: passes ONLY when a later-applied patch genuinely added the file. Returns the resulting commit SHA — the base a patch is rooted at.</summary>
        public async Task<string> SeedMarkerCheckAsync(string markerFile)
        {
            await File.WriteAllTextAsync(Path.Combine(_seed, "check.sh"), $"#!/bin/sh\nif [ -f {markerFile} ]; then exit 0; else exit 1; fi\n");
            await Git(_seed, "add", "-A");
            await Git(_seed, "commit", "-m", "add marker check");
            await Git(_seed, "push", "origin", "main");

            var rev = await new Core.Services.Agents.Sandbox.Runners.LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = new[] { "-C", _seed, "rev-parse", "HEAD" }, TimeoutSeconds = 30 }, CancellationToken.None);
            return rev.Stdout.Trim();
        }

        /// <summary>A real unified diff rooted at <paramref name="baseSha"/> — a fresh clone, <paramref name="mutate"/>, then <c>git diff --cached</c> against the base — the same mechanism a real agent's own capture uses. Never touches <c>_seed</c>'s own state.</summary>
        public async Task<string> MakePatchAsync(string baseSha, Action<string> mutate)
        {
            var work = Path.Combine(_root, "patch-" + Guid.NewGuid().ToString("N"));
            await Git(_root, "clone", _bare, work);
            await Config(work);
            await Git(work, "checkout", "--detach", baseSha);
            mutate(work);
            await Git(work, "add", "-A");

            var diff = await new Core.Services.Agents.Sandbox.Runners.LocalProcessRunner().RunAsync(
                new SandboxSpec { Command = "git", Args = new[] { "-C", work, "diff", "--cached", "--no-color", baseSha }, TimeoutSeconds = 30 }, CancellationToken.None);

            Directory.Delete(work, recursive: true);
            return diff.Stdout;
        }

        private static async Task Config(string dir)
        {
            await Git(dir, "config", "user.email", "test@codespace.dev");
            await Git(dir, "config", "user.name", "Test");
            await Git(dir, "config", "commit.gpgsign", "false");
        }

        private static async Task Git(string workdir, params string[] args)
        {
            var result = await new Core.Services.Agents.Sandbox.Runners.LocalProcessRunner().RunAsync(new SandboxSpec { Command = "git", Args = args, WorkingDirectory = workdir, TimeoutSeconds = 60 }, CancellationToken.None);
            if (result.Status != SandboxStatus.Success) throw new InvalidOperationException($"git {string.Join(' ', args)} failed (exit {result.ExitCode}): {result.Stderr}");
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
        }
    }

    private async Task<SupervisorTurnContext> RehydrateAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig, RecordingGrader grader)
    {
        using var scope = _fixture.BeginScope();
        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            scope.Resolve<ISupervisorDecider>(),
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            grader,
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(), scope.Resolve<ILogger<SupervisorTurnService>>());

        return await service.RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig, CancellationToken.None);
    }

    private async Task<(SupervisorTurnResult Result, RecordingGrader Grader)> RunStopTurnAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig, string[] command, BenchmarkGrade grade)
    {
        var grader = new RecordingGrader(grade);
        var result = await RunStopTurnWithGraderAsync(runId, teamId, goalConfig, command, grader);
        return (result, grader);
    }

    /// <summary>Run a terminal stop turn with a CUSTOM grader at the acceptance seam — lets a C1 test assert the AND across the operator floor + the model gate graded in order. An optional <paramref name="modelKind"/> is the oracle the model authors on ITS gate (the operator floor is always graded TestsPass server-side).</summary>
    private async Task<SupervisorTurnResult> RunStopTurnWithGraderAsync(Guid runId, Guid teamId, SupervisorGoalConfig goalConfig, string[] command, ISupervisorAcceptanceGrader grader, BenchmarkGradingKind? modelKind = null)
    {
        using var scope = _fixture.BeginScope();
        var service = new SupervisorTurnService(
            scope.Resolve<ISupervisorDecisionLog>(),
            new StopWithAcceptanceDecider(command, modelKind),
            scope.Resolve<ISupervisorActionExecutor>(),
            scope.Resolve<CodeSpaceDbContext>(),
            grader,
            scope.Resolve<IDecisionQueueService>(),
            scope.Resolve<IDecisionArbiter>(),
            scope.Resolve<IDecisionAnswerService>(),
            scope.Resolve<CodeSpace.Core.Services.Plans.IWorkPlanService>(),
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(), scope.Resolve<ILogger<SupervisorTurnService>>());

        return await service.RunTurnAsync(runId, teamId, NodeId, Goal, conversationId: null, goalConfig, CancellationToken.None);
    }

    private async Task SeedCleanMergeDecisionAsync(Guid runId, Guid teamId, string integratedBranch)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.Merge, IdempotencyKey = $"merge-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
            OutcomeJson = JsonSerializer.Serialize(new { integration = new { status = "Clean", integratedBranch } }, AgentJson.Options),
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Seed a MULTI-repo clean merge — the per-repo <c>integration.repositories[]</c> shape ReadFinalRepositoryBranches reads, with NO top-level integratedBranch (so ReadFinalIntegratedBranch is null and the stop grades the per-repo heads). A null repoId omits the id (an un-gradeable head).</summary>
    private async Task SeedMultiRepoMergeDecisionAsync(Guid runId, Guid teamId, params (Guid? RepoId, string Alias, string Branch)[] repos)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        var repositories = repos
            .Select(r => r.RepoId is { } id
                ? (object)new { status = "Clean", integratedBranch = r.Branch, repositoryId = id.ToString(), alias = r.Alias }
                : new { status = "Clean", integratedBranch = r.Branch, alias = r.Alias })
            .ToArray();

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.Merge, IdempotencyKey = $"merge-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}",
            OutcomeJson = JsonSerializer.Serialize(new { integration = new { status = "Clean", repositories } }, AgentJson.Options),
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> StopLedgerOutcomeAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Stop)
            .Select(d => d.OutcomeJson)
            .SingleAsync();
    }

    /// <summary>A decider that STOPS with an optional model-authored acceptance command (empty ⇒ no acceptance) and an optional oracle <c>kind</c> — drives the L4 P1 stop-grade path over the real ledger.</summary>
    private sealed class StopWithAcceptanceDecider : ISupervisorDecider
    {
        private readonly string[] _command;
        private readonly BenchmarkGradingKind? _kind;

        public StopWithAcceptanceDecider(string[] command, BenchmarkGradingKind? kind = null) { _command = command; _kind = kind; }

        public Task<SupervisorDecision> DecideAsync(SupervisorTurnContext context, CancellationToken cancellationToken) =>
            Task.FromResult(new SupervisorDecision
            {
                Kind = SupervisorDecisionKinds.Stop,
                PayloadJson = JsonSerializer.Serialize(new SupervisorStopPayload
                {
                    Outcome = "completed",
                    Summary = "done",
                    Acceptance = _command.Length > 0 ? new SupervisorAcceptanceSpec { Command = _command, Kind = _kind } : null,
                }, AgentJson.Options),
            });
    }

    private static SupervisorGoalConfig GoalConfig(Guid repoId, IReadOnlyList<string>? acceptanceChecks) => new()
    {
        Goal = Goal,
        AcceptanceChecks = acceptanceChecks,
        AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId },
    };

    private static string ResolveOutcome(string producedBranch, bool markerPresent)
    {
        var agentResults = new[] { new { agentRunId = Guid.NewGuid(), status = "Succeeded", summary = markerPresent ? $"reconciled {Marker}" : "reconciled", producedBranch } };

        return JsonSerializer.Serialize(new { agentRunIds = new[] { Guid.NewGuid() }, agentCount = 1, agentResults }, AgentJson.Options);
    }

    /// <summary>Like <see cref="ResolveOutcome"/> but with a CALLER-CONTROLLED agent run id (so a matching manifest can be seeded) and an optional (nullable) produced branch — S2's branch-less resolver scenarios.</summary>
    private static string ResolveOutcomeWithAgentId(Guid agentRunId, string? producedBranch, bool markerPresent)
    {
        var agentResults = new[] { new { agentRunId, status = "Succeeded", summary = markerPresent ? $"reconciled {Marker}" : "reconciled", producedBranch } };

        return JsonSerializer.Serialize(new { agentRunIds = new[] { agentRunId }, agentCount = 1, agentResults }, AgentJson.Options);
    }

    /// <summary>Seed a manifest row directly (bypassing IPublishManifestStore) — the durable source S2's patch fallback reads for a branch-less resolver, mirroring SupervisorUnitAcceptanceFoldFlowTests' own helper.</summary>
    private async Task SeedManifestAsync(Guid teamId, Guid agentRunId, Guid repositoryId, string? branch, string? baseSha, Guid? patchArtifactId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
            RepositoryAlias = "primary", Branch = branch, BaseSha = baseSha, PatchArtifactId = patchArtifactId,
            PublishStateValue = branch is not null ? PublishState.Pushed : PublishState.PatchOnly,
        });

        await db.SaveChangesAsync();
    }

    private static string MultiRepoResolveOutcome()
    {
        // A multi-repo resolver result: per-repo RepositoryResults present (the discriminator A3 + ResolvedBranch use).
        var result = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(),
            Status = "Succeeded",
            Summary = $"reconciled {Marker}",
            ProducedBranch = "primary",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), ProducedBranch = "web/x", BaseBranch = "main", ChangedFiles = new[] { "a" }, Access = WorkspaceAccess.Write },
            },
        };

        return JsonSerializer.Serialize(new { agentRunIds = new[] { Guid.NewGuid() }, agentCount = 1, agentResults = new[] { result } }, AgentJson.Options);
    }

    private async Task<Guid> SeedSupervisorRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateSupervisorWorkflowAsync(teamId, userId);
        return await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
    }

    private async Task<Guid> CreateSupervisorWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "sup-accept-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new Messages.Dtos.Workflows.WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<Messages.Dtos.Workflows.NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = NodeId, TypeKey = "agent.supervisor", Config = WorkflowsTestSeed.Json("""{"goal":"ship the feature"}"""), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<Messages.Dtos.Workflows.EdgeDefinition>
                {
                    new() { From = "start", To = NodeId },
                    new() { From = NodeId, To = "end" },
                },
            },
            Activations = new List<Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task SeedResolveDecisionAsync(Guid runId, Guid teamId, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            SupervisorRunId = runId,
            Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.Resolve,
            IdempotencyKey = $"resolve-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded,
            PayloadJson = "{}",
            OutcomeJson = outcomeJson,
            FenceEpoch = 1,
            CreatedDate = now,
            CreatedBy = Guid.Empty,
            LastModifiedDate = now,
            LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> LedgerOutcomeAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Resolve)
            .Select(d => d.OutcomeJson)
            .SingleAsync();
    }

    /// <summary>Records each grade call (count + args) and returns a canned grade — or throws (the unexpected-failure path). The seam A3 grades at, faked so the test asserts call count (the replay-once contract) without real git.</summary>
    /// <summary>P3.5 — a stub grader that captures the AMBIENT <c>LlmCallContext</c> at grade time, proving the acceptance-grading seam is wrapped in a "grader.acceptance"-labeled scope (the seam a real LlmJudgeGrader model call would ride, so its spend lands on the ledger + counts toward the cost cap).</summary>
    private sealed class ScopeCapturingGrader : ISupervisorAcceptanceGrader
    {
        private readonly BenchmarkGrade _grade;

        public ScopeCapturingGrader(BenchmarkGrade grade) => _grade = grade;

        public string? CapturedKind { get; private set; }
        public Guid? CapturedRunId { get; private set; }
        public Guid? CapturedTeamId { get; private set; }

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            Capture();
            return Task.FromResult(_grade);
        }

        public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            Capture();
            return Task.FromResult(_grade);
        }

        public Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) =>
            Task.FromResult(new BenchmarkGrade { Passed = true, Detail = "baseline-tests-passed" });

        private void Capture()
        {
            var scope = CodeSpace.Core.Services.Workflows.Llm.LlmCallContext.Current;
            CapturedKind = scope?.Kind;
            CapturedRunId = scope?.RunId;
            CapturedTeamId = scope?.TeamId;
        }
    }

    private sealed class RecordingGrader : ISupervisorAcceptanceGrader
    {
        private readonly BenchmarkGrade _grade;
        private readonly Exception? _throw;

        public RecordingGrader(BenchmarkGrade grade) => _grade = grade;
        public RecordingGrader(Exception toThrow) { _throw = toThrow; _grade = new BenchmarkGrade { Passed = false, Detail = "unused" }; }

        public int CallCount { get; private set; }
        public (Guid RepositoryId, Guid TeamId, string Branch, IReadOnlyList<string> Command, int TimeoutSeconds, BenchmarkGradingKind Kind)? LastCall { get; private set; }

        public int PatchCallCount { get; private set; }

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            CallCount++;
            LastCall = (repositoryId, teamId, branch, spec.Command, timeoutSeconds, spec.Kind ?? BenchmarkGradingKind.TestsPass);
            if (_throw != null) throw _throw;
            return Task.FromResult(_grade);
        }

        public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            PatchCallCount++;
            if (_throw != null) throw _throw;
            return Task.FromResult(_grade);
        }

        public Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) =>
            Task.FromResult(new BenchmarkGrade { Passed = true, Detail = "baseline-tests-passed" });
    }

    /// <summary>Returns a SEQUENCE of grades (one per call) + records each call's command — lets a C1 test assert the AND across the two stop gates graded in order (operator floor then model).</summary>
    private sealed class QueuedGrader : ISupervisorAcceptanceGrader
    {
        private readonly Queue<BenchmarkGrade> _grades;
        public QueuedGrader(params BenchmarkGrade[] grades) => _grades = new Queue<BenchmarkGrade>(grades);

        public int CallCount { get; private set; }
        public List<IReadOnlyList<string>> Commands { get; } = new();
        public List<BenchmarkGradingKind> Kinds { get; } = new();

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            CallCount++;
            Commands.Add(spec.Command);
            Kinds.Add(spec.Kind ?? BenchmarkGradingKind.TestsPass);
            return Task.FromResult(_grades.Count > 0 ? _grades.Dequeue() : new BenchmarkGrade { Passed = true, Detail = "default" });
        }

        public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            CallCount++;
            Commands.Add(spec.Command);
            Kinds.Add(spec.Kind ?? BenchmarkGradingKind.TestsPass);
            return Task.FromResult(_grades.Count > 0 ? _grades.Dequeue() : new BenchmarkGrade { Passed = true, Detail = "default" });
        }

        public Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken) =>
            Task.FromResult(new BenchmarkGrade { Passed = true, Detail = "baseline-tests-passed" });
    }
}

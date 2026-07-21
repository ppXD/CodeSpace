using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Agents.Eval;
using CodeSpace.Core.Services.Agents.Publish;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Decisions;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Agents;

/// <summary>
/// 🟢 High fidelity: the REAL <see cref="UnattendedDeliveryScorecardService"/> over real Postgres, composing the
/// REAL <c>IPublishManifestStore</c> / <c>IHumanTouchReader</c> / <c>ITeamCostService</c> — the exact same seams
/// production wires. Direct-seeds the ledger tables (PublishManifest / SupervisorDecisionRecord / ToolCallLedger /
/// AgentRun) rather than driving the full engine, mirroring <c>TeamCostServiceFlowTests</c> — the service under
/// test is a pure DB-read/aggregation layer, so seeding the tables it reads is the right fidelity tier (Rule 12),
/// not a lower one.
///
/// <para>Proves the north-star's exact definition: solved (objective acceptance grade, never self-report) AND
/// delivered (actually left the sandbox) AND zero human touches (neither an ask_human decision NOR an
/// approval-parked tool call) — each condition tested in isolation — plus tenancy, the since window, and the real
/// cost composition.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class UnattendedDeliveryScorecardFlowTests
{
    private readonly PostgresFixture _fixture;

    public UnattendedDeliveryScorecardFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_solved_delivered_zero_touch_run_scores_unattended()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);

        var card = await ComputeAsync(teamId);

        var run = card.Runs.Single(r => r.WorkflowRunId == runId);
        run.Solved.ShouldBeTrue();
        run.Delivered.ShouldBeTrue();
        run.HumanTouches.ShouldBe(0);
        run.UnattendedSolvedWithDelivery.ShouldBeTrue();

        card.Rollup.TotalRuns.ShouldBe(1);
        card.Rollup.UnattendedSolveWithDeliveryRate.ShouldBe(1.0);
    }

    [Fact]
    public async Task A_solved_but_undelivered_run_is_not_unattended()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        // Graded Passed but never pushed (PatchOnly by policy, or a failed push) — no PR either.
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.PatchOnly);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.Solved.ShouldBeTrue();
        run.Delivered.ShouldBeFalse("the diff never left the sandbox");
        run.UnattendedSolvedWithDelivery.ShouldBeFalse();
    }

    [Fact]
    public async Task A_run_with_one_failed_repo_manifest_is_not_solved_even_if_another_repo_passed()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed, alias: "repo-a");
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Failed, PublishState.PatchOnly, alias: "repo-b");

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.Solved.ShouldBeFalse("a mixed multi-repo result where ANY repo's acceptance failed must never be scored solved");
        run.UnattendedSolvedWithDelivery.ShouldBeFalse();
    }

    [Fact]
    public async Task A_pr_opened_without_reaching_pushed_state_still_counts_as_delivered()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.PatchOnly, prNumber: 42);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.Delivered.ShouldBeTrue("an opened PR/MR is a stronger delivery signal than PublishState alone — it must count even when PublishStateValue never reached Pushed");
        run.UnattendedSolvedWithDelivery.ShouldBeTrue();
    }

    [Fact]
    public async Task A_success_run_with_no_configured_acceptance_oracle_falls_back_to_its_own_terminal_status()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        // NotApplicable — no acceptance check was ever configured for this repo, the COMMON case.
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.NotApplicable, PublishState.Pushed);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.Solved.ShouldBeTrue("a run with no configured oracle that genuinely reached WorkflowRunStatus.Success must not be penalized for having no acceptance check at all");
        run.UnattendedSolvedWithDelivery.ShouldBeTrue();
    }

    [Fact]
    public async Task A_failed_run_with_no_configured_acceptance_oracle_is_not_solved()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Failure);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.NotApplicable, PublishState.PatchOnly);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.Solved.ShouldBeFalse("the run's own honest terminal status is Failure — nothing graded it either way, but the fallback must never call a failed run solved");
    }

    [Theory]
    [InlineData("{\"reason\":\"no progress\"}", "{}", false)]                                          // forced stop → never a fallback solve
    [InlineData("{}", "{\"stopped\":true,\"outcome\":\"could-not-finish\",\"summary\":\"s\"}", false)]  // model give-up → never a fallback solve
    [InlineData("{}", "{\"stopped\":true,\"outcome\":\"success\",\"summary\":\"s\"}", true)]           // clean succeeded stop → fallback stands
    public async Task A_degraded_supervisor_stop_never_reads_solved_through_the_status_fallback(string stopPayload, string stopOutcome, bool expectedSolved)
    {
        // P2b-prep metric-shift, pinned: degraded stops land engine Success BY DESIGN (AgentSupervisorNode returns
        // Ok for every terminal turn) — THE north-star inflation both audits named. An oracle verdict still
        // overrides in both directions; only the no-oracle fallback changes.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.NotApplicable, PublishState.Pushed);
        await SeedStopDecisionAsync(teamId, runId, stopPayload, stopOutcome);

        (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId).Solved.ShouldBe(expectedSolved);
    }

    [Fact]
    public async Task An_oracle_verdict_still_overrides_a_degraded_stop_in_both_directions()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedStopDecisionAsync(teamId, runId, "{\"reason\":\"no progress\"}", "{}");

        (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId).Solved
            .ShouldBeTrue("a manifest graded Passed is an oracle that RAN — it outranks the stop classification");
    }

    private async Task SeedStopDecisionAsync(Guid teamId, Guid runId, string payloadJson, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new CodeSpace.Core.Persistence.Entities.SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = 99,
            DecisionKind = SupervisorDecisionKinds.Stop, IdempotencyKey = $"stop-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_pre_protocol_run_is_visible_but_never_scored()
    {
        // Era-aware denominator (option c): a rate names exactly what it was measured over — a legacy run counts
        // in LegacyRuns, never in TotalRuns or any rate; old tape is never re-derived into a verdict.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var legacyId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success, contractEra: false);
        var modernId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, modernId, PublishAcceptanceState.Passed, PublishState.Pushed);

        var card = await ComputeAsync(teamId);

        card.Rollup.LegacyRuns.ShouldBe(1);
        card.Rollup.TotalRuns.ShouldBe(1, "the denominator is contract-era ONLY");
        card.Runs.ShouldAllBe(r => r.WorkflowRunId != legacyId);
    }

    [Fact]
    public async Task The_assessment_columns_ride_beside_the_legacy_ladder_without_touching_it()
    {
        // P4-U4 dual-read parity dashboard: the assessment-based counts sit BESIDE the legacy rates — the
        // standing consumer-switch evidence. Three contract-era runs: one assessed Solved+CleanSuccess, one
        // assessed Unsolved (the legacy ladder still reads it Solved — THE delta, visible), one not yet swept.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var clean = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        var inflated = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        var unswept = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, clean, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedManifestAsync(teamId, inflated, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedManifestAsync(teamId, unswept, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedAssessmentAsync(teamId, clean, outcome: "Solved", wouldBe: "CleanSuccess");
        await SeedAssessmentAsync(teamId, inflated, outcome: "Unsolved", wouldBe: "HonestFailure");

        var card = await ComputeAsync(teamId);

        card.Rollup.AssessedRuns.ShouldBe(2, "only runs with a durable shadow row are in the assessment columns");
        card.Rollup.AssessmentSolvedRuns.ShouldBe(1);
        card.Rollup.WouldBeCleanSuccessRuns.ShouldBe(1);
        card.Rollup.SolvedRuns.ShouldBe(3, "the LEGACY ladder is untouched — the delta between the columns IS the consumer-switch evidence, never an invisible metric shift");
    }

    private async Task SeedAssessmentAsync(Guid teamId, Guid runId, string outcome, string wouldBe)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.CompletionAssessmentRecord.Add(new CodeSpace.Core.Persistence.Entities.CompletionAssessmentRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, WorkflowRunId = runId,
            EnforcementMode = "Shadow", Basis = "ContractDerived", Outcome = outcome, Verification = "Passed",
            AssessmentJson = "{}", LegacyIsSolved = true, WouldBeTerminalDecision = wouldBe,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_parked_population_is_surfaced_beside_the_terminal_denominator()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Suspended);
        await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);

        var card = await ComputeAsync(teamId);

        card.Rollup.SuspendedRuns.ShouldBe(1, "a park-heavy period must never silently flatter the rates");
        card.Rollup.TotalRuns.ShouldBe(1, "suspended runs stay out of the terminal denominator — they are surfaced, not scored");
    }

    [Fact]
    public async Task A_cancelled_run_is_in_the_terminal_population_and_is_not_solved()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Cancelled);

        var card = await ComputeAsync(teamId);

        var run = card.Runs.Single(r => r.WorkflowRunId == runId);
        run.Solved.ShouldBeFalse("a Cancelled run's terminal-status fallback must not fall into the Success-only solved branch");
        card.Rollup.TotalRuns.ShouldBe(1, "Cancelled is one of the three terminal statuses the population query includes");
    }

    [Fact]
    public async Task The_per_run_breakdown_is_capped_at_the_recent_run_limit()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        const int runs = UnattendedDeliveryScorecardService.RecentRunCap + 5;

        for (var i = 0; i < runs; i++)
        {
            var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
            await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        }

        var card = await ComputeAsync(teamId);

        card.Runs.Count.ShouldBe(UnattendedDeliveryScorecardService.RecentRunCap, "the per-run breakdown is bounded to the cap");
        card.Rollup.TotalRuns.ShouldBe(UnattendedDeliveryScorecardService.RecentRunCap, "the rollup is computed over exactly the capped set the service returns — there is no separate uncapped total, unlike TeamCostRollup");
    }

    [Fact]
    public async Task A_run_with_a_genuinely_parked_ask_human_decision_is_not_unattended_even_when_solved_and_delivered()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedAskHumanDecisionAsync(teamId, runId, outcomeJson: GenuineAskOutcome());

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.Solved.ShouldBeTrue();
        run.Delivered.ShouldBeTrue();
        run.HumanTouches.ShouldBe(1);
        run.UnattendedSolvedWithDelivery.ShouldBeFalse("the run posted a card + parked on a human — never unattended, regardless of the outcome");
    }

    [Theory]
    [InlineData("""{"askHuman":"rejected","reason":"the ask_human decision carried no question text"}""")]   // blank question — never posted a card
    [InlineData("""{"question":"q","askHuman":"no-conversation","answer":null}""")]                          // no usable conversation — degraded self-advance
    public async Task A_rejected_or_degraded_ask_human_decision_never_reached_a_human_and_does_not_count(string outcomeJson)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedAskHumanDecisionAsync(teamId, runId, outcomeJson);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(0, "a rejected/degraded ask_human self-advances without ever posting a card — it never reached a human");
        run.UnattendedSolvedWithDelivery.ShouldBeTrue();
    }

    [Fact]
    public async Task A_run_with_an_approval_parked_tool_call_is_not_unattended_even_when_solved_and_delivered()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        var agentRunId = await SeedAgentRunAsync(teamId, runId);
        await SeedApprovalParkedToolCallAsync(teamId, agentRunId, ToolCallLedgerStatus.AwaitingApproval);

        // A decoy: a DIFFERENT run's DIFFERENT agent run also has a parked approval — proves the join is scoped by
        // THIS run's AgentRunId, not merely "any team-scoped parked row" (a false-negative test would pass even if
        // the join were broken to count every parked row regardless of which run it belongs to).
        var decoyRunId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, decoyRunId, PublishAcceptanceState.Passed, PublishState.Pushed);
        var decoyAgentRunId = await SeedAgentRunAsync(teamId, decoyRunId);
        await SeedApprovalParkedToolCallAsync(teamId, decoyAgentRunId, ToolCallLedgerStatus.AwaitingApproval);

        var card = await ComputeAsync(teamId);

        var run = card.Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(1, "the ledger join through AgentRun.WorkflowRunId must find the parked call for THIS run only");
        run.UnattendedSolvedWithDelivery.ShouldBeFalse();

        card.Runs.Single(r => r.WorkflowRunId == decoyRunId).HumanTouches.ShouldBe(1, "the decoy's OWN touch must not bleed onto the run under test, and vice versa");
    }

    [Fact]
    public async Task A_timeout_expired_approval_never_had_a_human_decide_and_does_not_count()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        var agentRunId = await SeedAgentRunAsync(teamId, runId);
        await SeedApprovalParkedToolCallAsync(teamId, agentRunId, ToolCallLedgerStatus.Expired);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(0, "the timeout reaper's Expired resolution never involved a person");
        run.UnattendedSolvedWithDelivery.ShouldBeTrue();
    }

    [Theory]
    [InlineData(DecisionAnsweredByKinds.Supervisor, 0)]   // the D4 arbiter auto-answered — zero human involvement
    [InlineData(DecisionAnsweredByKinds.Timeout, 0)]      // the bounded-wait deadline applied the default — nobody answered
    [InlineData(DecisionAnsweredByKinds.Human, 1)]        // a person actually answered from the "Needs decision" queue
    public async Task A_decision_request_calls_touch_count_depends_on_who_actually_answered_it(string answeredBy, int expectedTouches)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        var agentRunId = await SeedAgentRunAsync(teamId, runId);
        await SeedDecisionRequestAsync(teamId, agentRunId, answeredBy);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(expectedTouches, $"AnsweredBy={answeredBy} must map to {expectedTouches} touch(es)");
        run.UnattendedSolvedWithDelivery.ShouldBe(expectedTouches == 0);
    }

    [Fact]
    public async Task A_room_opened_pull_request_counts_as_a_human_touch()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.PatchOnly);
        // A real ICurrentUser scope — mirrors RoomPullRequestService.OpenAsync running inside an authenticated
        // HTTP request, whose SaveChangesAsync stamps CreatedBy with the REAL clicking user (CodeSpaceDbContext.
        // ApplyAuditFields), never SystemUsers.SeederId.
        await SeedIntegrationPullRequestManifestAsync(teamId, runId, actorUserId: userId);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(1, "a human clicking Room's Open-PR action is a genuine touch, even though it posts no ask_human card, approval, or node wait");
        run.UnattendedSolvedWithDelivery.ShouldBeFalse("a human-assisted delivery must never score as unattended");
    }

    [Fact]
    public async Task A_system_recorded_pull_request_with_no_human_actor_does_not_count_as_a_touch()
    {
        // The discriminator is WHO opened it, not whether a PR number exists — a background/engine-authored write
        // (no HTTP context, e.g. a future server-side deliver-at-stop) stamps CreatedBy = SystemUsers.SeederId via
        // BackgroundSeederUser, and must NOT be miscounted as a human touch the way a bare PullRequestNumber check
        // would.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.PatchOnly);
        await SeedIntegrationPullRequestManifestAsync(teamId, runId, actorUserId: null);

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(0, "a system-authored PR-open must not be counted as human involvement");
        run.UnattendedSolvedWithDelivery.ShouldBeTrue("a genuinely unattended delivery — including its PR — must still score unattended");
    }

    [Fact]
    public async Task A_room_opened_pull_request_touch_is_scoped_to_its_own_run_not_a_decoy()
    {
        // Mirrors the approval-count source's own decoy test (below) — the PR-touch source has no join, so a
        // dropped/weakened WHERE filter (workflowRunIds.Contains / TeamId) could silently leak a touch across runs
        // with nothing else in the suite catching it.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.PatchOnly);
        await SeedIntegrationPullRequestManifestAsync(teamId, runId, actorUserId: userId);

        var decoyRunId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, decoyRunId, PublishAcceptanceState.Passed, PublishState.PatchOnly);
        await SeedIntegrationPullRequestManifestAsync(teamId, decoyRunId, actorUserId: userId);

        var card = await ComputeAsync(teamId);

        card.Runs.Single(r => r.WorkflowRunId == runId).HumanTouches.ShouldBe(1, "this run's own PR-open touch must be found");
        card.Runs.Single(r => r.WorkflowRunId == decoyRunId).HumanTouches.ShouldBe(1, "the decoy's OWN touch must not bleed onto the run under test, and vice versa — each run counts its own PR-open exactly once");
    }

    [Fact]
    public async Task A_later_system_triggered_rebind_of_the_same_pr_row_does_not_erase_the_original_human_actor()
    {
        // The discriminator's fragile point (found by the M-1-style sweep): PublishManifestStore.UpsertAsync's
        // repeat-write path is a bulk ExecuteUpdateAsync that bypasses SaveChangesAsync/ApplyAuditFields entirely —
        // CreatedBy survives an update ONLY because its SetProperty list never assigns it. This proves that
        // invariant holds through the REAL store, not just by reading the SetProperty list: a human opens a PR
        // first (real insert, real ApiUser-shaped actor), then a LATER system-scoped call updates the SAME
        // (WorkflowRunId, RepositoryAlias) row (a rebind to a fresh branch) — the row's CreatedBy, and therefore
        // its human-touch classification, must survive unchanged.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.PatchOnly);

        await UpsertIntegrationPullRequestAsync(teamId, runId, actorUserId: userId, pullRequestNumber: 7, branch: "codespace/integration/turn1");
        await UpsertIntegrationPullRequestAsync(teamId, runId, actorUserId: null, pullRequestNumber: 8, branch: "codespace/integration/turn2");

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(1, "the row's ORIGINAL human actor must still be the one the touch count reads — a later system-scoped rebind must not silently erase it");
    }

    [Fact]
    public async Task A_flow_wait_approval_node_resolution_is_unconditionally_a_human_touch()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedNodeWaitAsync(teamId, runId, WorkflowWaitKinds.Approval, payloadJson: """{"approved":true}""");

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(1, "flow.wait_approval has no auto-resolution path — resolving it is always a human action");
        run.UnattendedSolvedWithDelivery.ShouldBeFalse();
    }

    [Theory]
    [InlineData(DecisionAnsweredByKinds.Supervisor, 0)]
    [InlineData(DecisionAnsweredByKinds.Human, 1)]
    public async Task A_flow_decision_node_touch_count_depends_on_who_actually_answered_it(string answeredBy, int expectedTouches)
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedNodeWaitAsync(teamId, runId, WorkflowWaitKinds.Decision, payloadJson: DecisionAnswerJson(answeredBy));

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.HumanTouches.ShouldBe(expectedTouches, $"a flow.decision node answered by {answeredBy} must map to {expectedTouches} touch(es)");
    }

    [Fact]
    public async Task A_run_with_no_manifest_rows_is_neither_solved_nor_delivered_but_still_counted()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        // A run that crashed before ever capturing a diff — the honest zero, not an exclusion.
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Failure);

        var card = await ComputeAsync(teamId);

        var run = card.Runs.Single(r => r.WorkflowRunId == runId);
        run.Solved.ShouldBeFalse();
        run.Delivered.ShouldBeFalse();
        run.UnattendedSolvedWithDelivery.ShouldBeFalse();
        card.Rollup.TotalRuns.ShouldBe(1, "the run counts in the denominator even though it produced nothing");
    }

    [Fact]
    public async Task Cost_is_composed_from_the_real_team_cost_service_bulk_read()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamId, runId, PublishAcceptanceState.Passed, PublishState.Pushed);
        await SeedTerminalPricedAgentAsync(teamId, runId, model: "claude-opus-4-8", input: 1_000_000, output: 0);   // $5

        var run = (await ComputeAsync(teamId)).Runs.Single(r => r.WorkflowRunId == runId);
        run.CostUsd.ShouldBe(5m, "the scorecard's cost must come from the REAL ITeamCostService pricing, not a stub");
    }

    [Fact]
    public async Task A_different_team_sees_none_of_another_teams_runs()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runA = await SeedTerminalRunAsync(teamA, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamA, runA, PublishAcceptanceState.Passed, PublishState.Pushed);

        (await ComputeAsync(teamA)).Runs.ShouldContain(r => r.WorkflowRunId == runA);

        var cardB = await ComputeAsync(teamB);
        cardB.Runs.ShouldBeEmpty("team B has no runs of its own");
        cardB.Runs.ShouldNotContain(r => r.WorkflowRunId == runA, "team A's run must never enter team B's scorecard");
        cardB.Rollup.TotalRuns.ShouldBe(0);
    }

    [Fact]
    public async Task The_since_filter_windows_on_created_date()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var recent = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success, createdAt: DateTimeOffset.UtcNow.AddHours(-1));
        var old = await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Success, createdAt: DateTimeOffset.UtcNow.AddDays(-30));

        var card = await ComputeAsync(teamId, since: DateTimeOffset.UtcNow.AddDays(-7));

        card.Runs.ShouldContain(r => r.WorkflowRunId == recent, "the 1-hour-old run is inside the 7-day window");
        card.Runs.ShouldNotContain(r => r.WorkflowRunId == old, "the 30-day-old run is before the window — excluded");
    }

    [Fact]
    public async Task An_in_flight_run_is_not_yet_in_the_population()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        await SeedTerminalRunAsync(teamId, WorkflowRunStatus.Running);

        (await ComputeAsync(teamId)).Rollup.TotalRuns.ShouldBe(0, "a still-running run has not yet had the chance to deliver");
    }

    [Fact]
    public async Task The_team_scoped_query_handler_returns_only_the_callers_team_scorecard()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var runA = await SeedTerminalRunAsync(teamA, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamA, runA, PublishAcceptanceState.Passed, PublishState.Pushed);
        var runB = await SeedTerminalRunAsync(teamB, WorkflowRunStatus.Success);
        await SeedManifestAsync(teamB, runB, PublishAcceptanceState.Failed, PublishState.PatchOnly);

        using var scope = _fixture.BeginScopeAs(userA, teamA, Roles.Admin);
        var card = await scope.Resolve<IMediator>().Send(new GetUnattendedDeliveryScorecardQuery { Since = null });

        card.Runs.ShouldContain(r => r.WorkflowRunId == runA);
        card.Runs.ShouldNotContain(r => r.WorkflowRunId == runB, "the handler scopes to the caller's team via ICurrentTeam — team B's run must never leak");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private async Task<UnattendedDeliveryScorecard> ComputeAsync(Guid teamId, DateTimeOffset? since = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IUnattendedDeliveryScorecardService>().ComputeAsync(teamId, since, CancellationToken.None);
    }

    /// <summary>A snapshot-style (WorkflowId-less) terminal run — the single-agent lane's shape. Passing an explicit <paramref name="createdAt"/> survives the auditing interceptor (which only stamps CreatedDate when it is the default value).</summary>
    private async Task<Guid> SeedTerminalRunAsync(Guid teamId, WorkflowRunStatus status, DateTimeOffset? createdAt = null, bool contractEra = true)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId,
            TeamId = teamId,
            SourceType = WorkflowRunSourceTypes.Manual,
            ActorType = "user",
            ActorId = SystemUsers.SeederId,
            NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed,
            ReceivedAt = now,
            VerifiedAt = now,
            NormalizedAt = now,
        });

        db.WorkflowRun.Add(new WorkflowRun
        {
            CompletionPolicyVersion = contractEra ? Core.Services.Completion.CompletionPolicy.CurrentVersion : null,
            CompletionEnforcementMode = contractEra ? Core.Services.Completion.CompletionPolicy.CurrentMode.ToString() : null,
            Id = runId,
            TeamId = teamId,
            RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Manual,
            Status = status,
            CompletedAt = now,
            CreatedBy = SystemUsers.SeederId,
            LastModifiedBy = SystemUsers.SeederId,
            CreatedDate = createdAt ?? default,
        });

        await db.SaveChangesAsync();
        return runId;
    }

    private async Task SeedManifestAsync(Guid teamId, Guid runId, PublishAcceptanceState acceptance, PublishState publishState, string alias = "primary", int? prNumber = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Kind = PublishManifestKind.Integration,
            WorkflowRunId = runId,
            RepositoryAlias = alias,
            AcceptanceState = acceptance,
            PublishStateValue = publishState,
            PullRequestNumber = prNumber,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A SECOND Integration-kind manifest row (a distinct alias, so it doesn't collide with <see cref="SeedManifestAsync"/>'s
    /// "primary" row) carrying a PR — seeded through a scope whose <c>ICurrentUser</c> is EITHER a real actor
    /// (<paramref name="actorUserId"/> non-null, mirroring a Room-clicked open) OR the ambient default (null,
    /// mirroring a background/system write) — so <c>CodeSpaceDbContext.ApplyAuditFields</c> stamps <c>CreatedBy</c>
    /// exactly as the real <c>RoomPullRequestService.OpenAsync</c> vs. a future server-authored open would.
    /// </summary>
    private async Task SeedIntegrationPullRequestManifestAsync(Guid teamId, Guid runId, Guid? actorUserId)
    {
        using var scope = actorUserId is { } uid ? _fixture.BeginScopeAs(uid, teamId, Roles.Admin) : _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Kind = PublishManifestKind.Integration,
            WorkflowRunId = runId,
            RepositoryAlias = "pr-touch-probe",
            AcceptanceState = PublishAcceptanceState.NotApplicable,
            PublishStateValue = PublishState.Pushed,
            PullRequestNumber = 7,
            PullRequestUrl = "https://example.test/pr/7",
        });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Same shape as <see cref="SeedIntegrationPullRequestManifestAsync"/> but through the REAL
    /// <see cref="IPublishManifestStore"/> (not a raw <see cref="CodeSpaceDbContext"/> add) — so a SECOND call for
    /// the same <paramref name="teamId"/>/<paramref name="runId"/>/alias genuinely exercises
    /// <c>PublishManifestStore.UpsertAsync</c>'s repeat-write <c>ExecuteUpdateAsync</c> path (the bulk SQL update
    /// that bypasses <c>ApplyAuditFields</c>), not just a second insert.
    /// </summary>
    private async Task UpsertIntegrationPullRequestAsync(Guid teamId, Guid runId, Guid? actorUserId, int pullRequestNumber, string branch)
    {
        using var scope = actorUserId is { } uid ? _fixture.BeginScopeAs(uid, teamId, Roles.Admin) : _fixture.BeginScope();

        await scope.Resolve<IPublishManifestStore>().UpsertForIntegrationAsync(new PublishManifestUpsert
        {
            TeamId = teamId,
            WorkflowRunId = runId,
            RepositoryAlias = "pr-touch-probe",
            Branch = branch,
            PublishStateValue = PublishState.Pushed,
            PullRequestNumber = pullRequestNumber,
            PullRequestUrl = $"https://example.test/pr/{pullRequestNumber}",
        }, CancellationToken.None);
    }

    private async Task SeedAskHumanDecisionAsync(Guid teamId, Guid runId, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = 1,
            DecisionKind = SupervisorDecisionKinds.AskHuman, IdempotencyKey = $"ask-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = """{"question":"proceed?"}""", OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>The recorded outcome shape a GENUINE ask_human park writes: <c>askHumanToken</c> present, mirroring <c>RealSupervisorActionExecutor.AskHuman.cs</c>'s <c>AskOutcome</c>.</summary>
    private static string GenuineAskOutcome() => """{"question":"proceed?","askHumanToken":"tok-1","answer":null}""";

    private async Task<Guid> SeedAgentRunAsync(Guid teamId, Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var agentRunId = Guid.NewGuid();

        db.AgentRun.Add(new AgentRun
        {
            Id = agentRunId,
            TeamId = teamId,
            WorkflowRunId = runId,
            Harness = "claude-code",
            Status = AgentRunStatus.Succeeded,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "g", Harness = "claude-code" }, AgentJson.Options),
        });

        await db.SaveChangesAsync();
        return agentRunId;
    }

    private async Task SeedApprovalParkedToolCallAsync(Guid teamId, Guid agentRunId, ToolCallLedgerStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            AgentRunId = agentRunId,
            ToolKind = "git.open_pr",
            IdempotencyKey = $"tc-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = status,
            ApprovalToken = Guid.NewGuid().ToString("N"),
            ApprovalDeadlineAt = DateTimeOffset.UtcNow.AddHours(1),
        });

        await db.SaveChangesAsync();
    }

    /// <summary>A resolved <c>decision.request</c> agent-grain call, stamping the SAME <see cref="DecisionAnswer"/> shape the Decision substrate writes on resolve — <see cref="DecisionAnswer.AnsweredBy"/> drives whether it's a genuine human touch.</summary>
    private async Task SeedDecisionRequestAsync(Guid teamId, Guid agentRunId, string answeredBy)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.ToolCallLedger.Add(new ToolCallLedger
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            AgentRunId = agentRunId,
            ToolKind = DecisionToolKinds.DecisionRequest,
            IdempotencyKey = $"decision-{Guid.NewGuid():N}",
            InputHash = "test",
            Status = ToolCallLedgerStatus.Succeeded,
            ApprovalToken = Guid.NewGuid().ToString("N"),
            ApprovalDeadlineAt = DateTimeOffset.UtcNow.AddHours(1),
            ResultJson = DecisionAnswerJson(answeredBy),
        });

        await db.SaveChangesAsync();
    }

    /// <summary>A node-grain <c>WorkflowRunWait</c> row (flow.wait_approval / flow.decision), Resolved with the given payload.</summary>
    private async Task SeedNodeWaitAsync(Guid teamId, Guid runId, string waitKind, string payloadJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = "node-1",
            WaitKind = waitKind,
            Token = Guid.NewGuid().ToString("N"),
            Status = WorkflowWaitStatuses.Resolved,
            PayloadJson = payloadJson,
            CreatedAt = DateTimeOffset.UtcNow,
            ResolvedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>A serialized <see cref="DecisionAnswer"/> answered by the given kind — the shape both the agent-grain (ToolCallLedger.ResultJson) and node-grain (WorkflowRunWait.PayloadJson, once resolved) resolutions share.</summary>
    private static string DecisionAnswerJson(string answeredBy) => JsonSerializer.Serialize(new DecisionAnswer
    {
        DecisionId = Guid.NewGuid(),
        AnsweredBy = answeredBy,
        Rationale = answeredBy == DecisionAnsweredByKinds.Human ? null : "auto-answered for the test",
    });

    private async Task SeedTerminalPricedAgentAsync(Guid teamId, Guid runId, string model, int input, int output)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.AgentRun.Add(new AgentRun
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            WorkflowRunId = runId,
            Harness = "claude-code",
            Status = AgentRunStatus.Succeeded,
            TaskJson = JsonSerializer.Serialize(new AgentTask { Goal = "g", Harness = "claude-code", Model = model }, AgentJson.Options),
            ResultJson = JsonSerializer.Serialize(new AgentRunResult
            {
                Status = AgentRunStatus.Succeeded,
                ExitReason = "completed",
                TokenUsage = new AgentTokenUsage { InputTokens = input, OutputTokens = output },
            }, AgentJson.Options),
        });

        await db.SaveChangesAsync();
    }
}

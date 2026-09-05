using CodeSpace.Core.Services.Agents.Publish;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Decisions;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.Core.Services.Supervisor.Arbiter;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Agents.Benchmark;
using CodeSpace.Messages.Dtos.Agents;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Shouldly;

using CodeSpace.Tests.Fakes;
namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres + the REAL <see cref="SupervisorTurnService"/>.<c>RehydrateFromDecisionLogAsync</c> with
/// a fake grader at the one acceptance seam): loopability slice 3 — the PER-UNIT objective acceptance fold. Each spawned
/// unit whose planned subtask authored an acceptance is graded on ITS OWN branch and the verdict folds onto that unit's
/// result. Proves: a FAILED unit folds rejected, is EXCLUDED from the no-progress evidence (the must-fix — proven through
/// the real FoldNoProgressDecisions), and renders the retry signal; a PASSED unit folds accepted; a unit with no contract
/// is byte-identical (never grades); the grade runs EXACTLY ONCE (replay reads the tape, never re-grades); a grader
/// failure fails closed; a MULTI-repo unit is graded against EVERY repo it actually changed (all-or-nothing, short-
/// circuiting on the first failing repo, skipping a repo with no changes, failing closed if none produced a branch
/// anywhere); and a repo-overridden unit is graded against ITS repo, not the profile default. The grader's real
/// clone+run fidelity is proven in <c>SupervisorAcceptanceFoldFlowTests</c> (same grader); this pins the per-unit FOLD
/// wiring (positional join, evidence discount, repo resolution) over real Postgres.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SupervisorUnitAcceptanceFoldFlowTests
{
    private readonly PostgresFixture _fixture;

    public SupervisorUnitAcceptanceFoldFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    private const string NodeId = "sup";
    private const string Goal = "ship the feature";
    private static readonly string[] Check = { "sh", "check.sh" };

    [Theory]
    [InlineData(true, true, 0)]    // a unit that PASSES its check → accepted, real evidence → streak resets to 0
    [InlineData(false, false, 2)]  // a unit that FAILS → rejected, NOT evidence → plan + spawn both count (the discount)
    public async Task A_unit_is_graded_on_its_branch_and_the_verdict_drives_evidence(bool gradePasses, bool expectedVerdict, int expectedNoProgress)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        var agentId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = gradePasses, Detail = gradePasses ? "tests-passed" : "tests-failed-exit-1" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(1, "the unit's acceptance command runs once against its own branch");
        grader.LastCall!.Value.RepositoryId.ShouldBe(repoId);
        grader.LastCall.Value.Branch.ShouldBe("codespace/agent/s1", "the unit is graded against the branch IT produced");
        grader.LastCall.Value.Command.ShouldBe(Check, "the subtask's authored acceptance command is the graded argv");
        grader.LastCall.Value.Kind.ShouldBe(BenchmarkGradingKind.TestsPass, "a subtask with no authored oracle kind defaults to TestsPass");

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().AcceptancePassed.ShouldBe(expectedVerdict, "the per-unit verdict folds onto the agent result");

        ctx.NoProgressDecisions.ShouldBe(expectedNoProgress, "a rejected unit is discounted from the settled evidence — an acceptance-failing wave is no progress");

        SupervisorOutcome.ReadAgentResults(await LedgerSpawnOutcomeAsync(runId, teamId)).Single().AcceptancePassed
            .ShouldBe(expectedVerdict, "the verdict is PERSISTED on the durable spawn row (replay reads it, never re-grades)");
    }

    [Fact]
    public async Task A_budget_refused_judge_leaves_the_unit_a_budget_skip_never_a_grader_fault()
    {
        // W-hard: the run's own cap refused the judge call mid-fold. The unit must NOT read grade-error (that
        // labels the instrument sick) and must NOT read a genuine failure (that blames the candidate and buys
        // retries no retry can afford) — it reads the budget-skip, infra-classed, and the rehydrate SURVIVES so
        // the decider's own guarded call can land the run's honest CostCapReached stop.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        var agentId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, "codespace/agent/s1")));

        var grader = new RecordingGrader(new CodeSpace.Core.Services.Workflows.Llm.LlmBudgetExceededException("grader.acceptance", committedUsd: 4.9m, capUsd: 5m));
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        var result = SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson).Single();
        result.AcceptancePassed.ShouldBe(false);
        result.AcceptanceDetail.ShouldBe(SupervisorTurnService.GradeSkippedBudgetExhausted);

        CodeSpace.Core.Services.Agents.AgentAcceptanceContract.IsInfraFailure(result.AcceptanceDetail, workPresent: true)
            .ShouldBeTrue("the check never ran — infra-classed, buys no retries, never blames the candidate");
    }

    // ── B-pre: the fold's verdict is written back to the unit's publish manifest ──────────────────────

    [Theory]
    [InlineData(true, PublishAcceptanceState.Passed)]
    [InlineData(false, PublishAcceptanceState.Failed)]
    public async Task A_folds_verdict_is_stamped_onto_the_units_publish_manifest(bool gradePasses, PublishAcceptanceState expected)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        var agentId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, "codespace/agent/s1")));
        await SeedManifestAsync(teamId, agentId, repoId, "codespace/agent/s1", baseSha: null, patchArtifactId: null);

        await RehydrateAsync(runId, teamId, GoalConfig(repoId), new RecordingGrader(new BenchmarkGrade { Passed = gradePasses, Detail = "graded" }));

        (await ManifestAcceptanceStateAsync(agentId)).ShouldBe(expected,
            "supervisor units are born NotApplicable (no AgentTask.Acceptance at completion) — manifest readers like the unattended scorecard's oracle leg see the fold's verdict only through the write-back");
    }

    [Fact]
    public async Task A_unit_without_a_contract_never_stamps_its_manifest()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", null)));
        var agentId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, "codespace/agent/s1")));
        await SeedManifestAsync(teamId, agentId, repoId, "codespace/agent/s1", baseSha: null, patchArtifactId: null);

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "unused" });
        await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(0, "no contract, no grade");
        (await ManifestAcceptanceStateAsync(agentId)).ShouldBe(PublishAcceptanceState.NotApplicable,
            "an ungraded unit keeps its birth state — NotApplicable still means UNGRADED, never a laundered verdict");
    }

    private async Task<PublishAcceptanceState> ManifestAcceptanceStateAsync(Guid agentRunId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().PublishManifest.AsNoTracking()
            .Where(m => m.AgentRunId == agentRunId).Select(m => m.AcceptanceState).SingleAsync();
    }

    // ── B3: the co-sign overlay — approved amendments change what the fold grades by ──────────────────

    [Fact]
    public async Task An_approved_amendment_regrades_the_unit_against_the_replacement_oracle()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedAmendCardAsync(runId, teamId, sequence: 2,
            new SupervisorAmendAcceptancePayload { SubtaskId = "s1", Reason = "check.sh invokes missing tooling", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "verify.sh" } } },
            answer: "approve — the replacement is right");
        await SeedSpawnAsync(runId, teamId, sequence: 3, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "verified" });
        await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.LastCall!.Value.Command.ShouldBe(new[] { "sh", "verify.sh" },
            "the fold grades by the co-signed EFFECTIVE oracle — the human-approved replacement, not the plan's superseded original");
    }

    [Fact]
    public async Task An_unanswered_amend_card_changes_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedAmendCardAsync(runId, teamId, sequence: 2,
            new SupervisorAmendAcceptancePayload { SubtaskId = "s1", Reason = "r", Acceptance = new SupervisorAcceptanceSpec { Command = new[] { "sh", "verify.sh" } } },
            answer: null);
        await SeedSpawnAsync(runId, teamId, sequence: 3, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "graded" });
        await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.LastCall!.Value.Command.ShouldBe(Check, "FATAL-2: a proposal without its own approving answer carries no authority");
    }

    [Fact]
    public async Task An_approved_waive_stamps_the_unit_and_its_manifest_without_grading()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedAmendCardAsync(runId, teamId, sequence: 2,
            new SupervisorAmendAcceptancePayload { SubtaskId = "s1", Reason = "no objective check is possible here", Waive = true },
            answer: "approve");
        var agentId = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 3, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, "codespace/agent/s1")));
        await SeedManifestAsync(teamId, agentId, repoId, "codespace/agent/s1", baseSha: null, patchArtifactId: null);

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "unused" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(0, "a waived unit is never graded — there is no oracle to run");

        var folded = SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson).Single();
        folded.AcceptanceVerdict.ShouldBe(CodeSpace.Messages.Contracts.VerificationDisposition.Waived, "the tape carries the DISTINCT waived state — never a pass, never silence");
        folded.AcceptancePassed.ShouldBeNull();

        (await ManifestAcceptanceStateAsync(agentId)).ShouldBe(PublishAcceptanceState.Waived,
            "the manifest mirror keeps the scorecard's oracle leg honest — a waived-only run never reads Solved (B2)");
    }

    private async Task SeedAmendCardAsync(Guid runId, Guid teamId, int sequence, SupervisorAmendAcceptancePayload payload, string? answer)
    {
        var card = SupervisorAmendAcceptance.IntoAskHuman(payload);
        var outcome = answer is null ? "{}" : JsonSerializer.Serialize(new { question = "q", answer }, CodeSpace.Core.Services.Agents.AgentJson.Options);

        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.AskHuman, card.PayloadJson!, outcome);
    }

    // ── P5-2 (diagnosis-driven repair): the failed check's OUTPUT TAIL rides the tape ─────────────────

    [Theory]
    [InlineData(false, "exit=1\nFAILED Foo.Bar: expected 42")]  // failed → the diagnosis folds onto the unit + persists
    [InlineData(true, null)]                                    // passed → nothing to repair, the tail is dropped (lean tape)
    public async Task A_failed_grades_evidence_tail_rides_the_tape_and_a_pass_stamps_none(bool gradePasses, string? expectedTail)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade
        {
            Passed = gradePasses, Detail = gradePasses ? "tests-passed" : "tests-failed-exit-1",
            EvidenceTail = "exit=1\nFAILED Foo.Bar: expected 42",
        });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        var folded = SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson).Single();
        folded.AcceptanceEvidenceTail.ShouldBe(expectedTail, "the diagnosis rides the tape ONLY on failure — the decider's render and the retry handoff both read it from here");

        SupervisorOutcome.ReadAgentResults(await LedgerSpawnOutcomeAsync(runId, teamId)).Single().AcceptanceEvidenceTail
            .ShouldBe(expectedTail, "the tail is PERSISTED on the durable spawn row — replay renders the same diagnosis without re-grading");
    }

    // ── P5-6: the rehydrated context carries the "if you stopped now" recital ─────────────────────────

    [Fact]
    public async Task A_contract_bearing_run_rehydrates_with_the_stopped_now_recital()
    {
        // The full mid-run chain over real Postgres: staked requirement + a fold that grades FAILED → the
        // rehydrated context's prerendered recital says a stop now cannot read Solved. The compose runs AFTER
        // the fold persisted its verdict, so the recital always reflects THIS turn's freshest facts.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        using (var stamp = _fixture.BeginScope())
        {
            var db = stamp.Resolve<CodeSpaceDbContext>();
            var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
            run.CompletionPolicyVersion = Core.Services.Completion.CompletionPolicy.CurrentVersion;
            run.CompletionEnforcementMode = Core.Services.Completion.CompletionPolicy.CurrentMode.ToString();
            await db.SaveChangesAsync();

            await stamp.Resolve<Core.Services.Completion.ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
            {
                new Messages.Contracts.RequirementEnvelope { RequirementRef = "acceptance:s1", Kind = Messages.Contracts.ContractKinds.Acceptance, Requiredness = Messages.Contracts.Requiredness.Required, Authority = Messages.Contracts.ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
            }, CancellationToken.None);
        }

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        ctx.CompletionRecital.ShouldNotBeNull("a contract-bearing post-F0 run recites the reducer's what-if to the decider");
        ctx.CompletionRecital!.ShouldContain("IF YOU STOPPED NOW", Case.Sensitive);
        ctx.CompletionRecital.ShouldContain("verification=Failed", Case.Sensitive, "the just-folded FAILED grade reached the recital in the same rehydrate");
        ctx.CompletionRecital.ShouldContain("cannot read Solved", Case.Sensitive);
    }

    /// <summary>
    /// The gap the whole-loop headline exposed (real-model runs 33930904059 / 33943475246): two units merged into a
    /// CONFLICT, the resolver's reconciliation came back UNVERIFIED, and the decider stopped 'completed' anyway —
    /// then <c>CompletionTerminalAuthority</c> refused the stop over an Integrate stage with no evidence and parked
    /// the run. The refusal is knowable a full turn earlier, and this drives the real rehydrate over that exact tape
    /// to pin that the NEXT turn's rendered prompt says so. That the line agrees with the authority's own verdict is
    /// pinned over the full CleanSuccess predicate in <c>CompletionTerminalAuthorityFlowTests</c>.
    ///
    /// <para>The COHORT is a parameter, because the refusal verb is one. The authority arbitrates only an Enforced
    /// run (<c>CompletionTerminalAuthority.cs:59</c>) and <c>CompletionPolicy.CurrentMode</c> is Shadow — so the
    /// stamped mode decides whether the same un-reconciled tape is told its stop WILL be refused or merely that a
    /// 'completed' claim would be recorded against evidence the profile declares owed. The facts do not move.</para>
    /// </summary>
    [Theory]
    [InlineData(false, "Enforced")]   // conflicted merge + unverified resolve, Enforced → nothing integrated → the stop really would be refused
    [InlineData(true, "Enforced")]    // the same run, merged clean → Integrate evidenced → no stage line at all
    [InlineData(false, "Shadow")]     // CompletionPolicy.CurrentMode — the DEFAULT cohort gets the facts, never the false threat
    [InlineData(true, "Shadow")]
    public async Task A_run_whose_branches_are_unreconciled_is_told_which_stage_it_has_not_evidenced(bool mergesClean, string enforcementMode)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await StampContractAsync(runId, teamId, enforcementMode);

        // No authored acceptance command on either unit — nothing for the fold to grade, so this run can go through
        // the CONTAINER-resolved turn service instead of a hand-built one. That is deliberate: the stage line only
        // reaches the prompt if Autofac actually supplies the profile registry, and a hand-built service would
        // answer that question by construction.
        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", null), ("s2", null)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1","s2"]}""",
            SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1"), Unit(Guid.NewGuid(), "codespace/agent/s2")));

        if (mergesClean)
        {
            await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Merge, "{}", CleanMergeOutcome);
        }
        else
        {
            await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Merge, "{}", ConflictedMergeOutcome);
            // The resolver ran and pushed, but self-reported NO verification — the exact tape the decider was told
            // to stop or ask over, and the one that leaves the run with no reviewable integrated head.
            await SeedDecisionAsync(runId, teamId, 4, SupervisorDecisionKinds.Resolve, "{}", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/resolve/head")));
        }

        using var scope = _fixture.BeginScope();
        var ctx = await scope.Resolve<ISupervisorTurnService>().RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, GoalConfig(Guid.NewGuid()), CancellationToken.None);
        var prompt = CodeSpace.Core.Services.Supervisor.Deciders.LlmSupervisorDecider.BuildUserPromptForTest(ctx);

        prompt.ShouldContain(CodeSpace.Core.Services.Supervisor.Deciders.SupervisorStopNowRecital.Header, Case.Sensitive, "the run is contract-bearing, so the block renders either way");

        prompt.Contains(CodeSpace.Core.Services.Supervisor.Deciders.SupervisorStopNowRecital.RefusalLead, StringComparison.Ordinal).ShouldBe(!mergesClean && enforcementMode == "Enforced",
            "the decider's prompt must warn about the refusal EXACTLY while an ENFORCED run's branches are un-reconciled — promising a stop the authority refuses and threatening a Shadow run with a refusal it can never receive are the same defect in opposite directions");

        prompt.Contains(CodeSpace.Core.Services.Supervisor.Deciders.SupervisorStopNowRecital.AdvisoryLead, StringComparison.Ordinal).ShouldBe(!mergesClean && enforcementMode != "Enforced",
            "the run's ENFORCEMENT MODE has to reach the renderer through the container too — a rehydrate that dropped it would fall back to the advisory everywhere, or to the threat everywhere");

        if (!mergesClean)
            prompt.ShouldContain("requires 1 stage(s) with no evidence — Integrate.", Case.Sensitive,
                "the un-reconciled branches are the missing evidence, and the line has to name the stage the park names — in both cohorts");
    }

    /// <summary>A CONFLICTED integration: nothing was combined, so no reviewable head exists and the Integrate cell stays unevidenced.</summary>
    private const string ConflictedMergeOutcome = """{"integration":{"status":"Conflicted","reason":"the agents edited the same file","outcomes":[{"conflictedFiles":["src/Feature.cs"],"fallbackBranch":"codespace/agent/s1"}]}}""";

    /// <summary>A CLEAN integration whose combined head is named — the Integrate cell's first ledger.</summary>
    private const string CleanMergeOutcome = """{"integration":{"status":"Clean","integratedBranch":"codespace/integration/head"}}""";

    /// <summary>Stamp the run post-F0 and stake the obligations a spawned wave would have staked — the shape the recital and the authority both read. <paramref name="enforcementMode"/> defaults to what production stamps a run that does not opt in (<c>CompletionPolicy.CurrentMode</c> — Shadow).</summary>
    private async Task StampContractAsync(Guid runId, Guid teamId, string? enforcementMode = null)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.CompletionPolicyVersion = Core.Services.Completion.CompletionPolicy.CurrentVersion;
        run.CompletionEnforcementMode = enforcementMode ?? Core.Services.Completion.CompletionPolicy.CurrentMode.ToString();
        await db.SaveChangesAsync();

        await scope.Resolve<Core.Services.Completion.ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new Messages.Contracts.RequirementEnvelope { RequirementRef = "acceptance:s1", Kind = Messages.Contracts.ContractKinds.Acceptance, Requiredness = Messages.Contracts.Requiredness.Required, Authority = Messages.Contracts.ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);
    }

    [Fact]
    public async Task A_run_without_a_stamped_policy_rehydrates_without_a_recital()
    {
        // The chassis's default run has no CompletionPolicyVersion (pre-F0 shape) — the recital must stay null
        // and every pre-existing rehydrate in this file stays byte-identical.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1" }));

        ctx.CompletionRecital.ShouldBeNull("LegacyUnknown runs recite nothing — the recital never fabricates a contract verdict");
    }

    // ── P4-1: the per-unit fold's contradiction classification (both directions + agreement) ──────────

    [Fact]
    public async Task A_unit_that_self_reports_succeeded_but_fails_its_check_folds_an_over_claim()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().Contradiction
            .ShouldBe(AgentContradiction.OverClaim, "the agent believed it was Succeeded; the objective check disagreed");
    }

    [Fact]
    public async Task A_unit_that_self_reports_failed_but_passes_its_check_folds_an_under_claim()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        var failedUnit = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Failed", Error = "the agent gave up", ProducedBranch = "codespace/agent/s1" };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(failedUnit));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(1, "the per-unit fold grades a unit regardless of its OWN self-reported status — unlike the single-agent lane, which only grades a would-be Succeeded result");

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var result = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single();
        result.AcceptancePassed.ShouldBe(true, "the objective grade is what it is, independent of the self-report");
        result.Contradiction.ShouldBe(AgentContradiction.UnderClaim, "the agent gave up on work that was actually fine");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_unit_whose_self_report_agrees_with_its_grade_folds_no_contradiction(bool gradePasses)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        // "Succeeded" + passing grade agree; "Failed" + failing grade agree — both are the honest, non-contradictory case.
        var unit = gradePasses
            ? Unit(Guid.NewGuid(), "codespace/agent/s1")
            : new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = "Failed", Error = "boom", ProducedBranch = "codespace/agent/s1" };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(unit));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = gradePasses, Detail = gradePasses ? "tests-passed" : "tests-failed-exit-1" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().Contradiction.ShouldBeNull();
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("TimedOut")]
    public async Task A_cancelled_or_timed_out_units_vacuous_pass_folds_no_contradiction(string status)
    {
        // P4-1 regression: a Cancelled/TimedOut unit never reached a genuine self-report at all — it must NOT be
        // folded as "self-reported failure" (which would durably mislabel it under_claim on a vacuous pass, as if
        // it had given up on work that was actually fine, when in truth it was killed mid-flight and verified nothing).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayloadWithExpectsChanges("s1", Check, expectsChanges: false));
        var killedUnit = new SupervisorAgentResult { AgentRunId = Guid.NewGuid(), Status = status, Error = "abandoned by the reconciler", ProducedBranch = null };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(killedUnit));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var result = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single();
        result.AcceptancePassed.ShouldBe(true, "the subtask declared no changes were expected, and none were produced — vacuously satisfied regardless of why the unit ended");
        result.Contradiction.ShouldBeNull($"a {status} unit never self-reported anything — it must not be stamped under_claim just because it wasn't a literal 'Succeeded'");
    }

    // ── S2: a branch-less unit's OWN recorded manifest (never the outcome-JSON snapshot — I2) ──────────

    [Fact]
    public async Task A_unit_with_no_branch_but_a_recorded_patch_grades_via_the_patch_not_fail_closed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, producedBranch: null)));
        await SeedManifestAsync(teamId, agentId, repoId, branch: null, baseSha: "deadbeef", patchArtifactId: Guid.NewGuid());

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(0, "no branch → the branch-based path is never invoked");
        grader.PatchCalls.Count.ShouldBe(1, "the unit's OWN recorded manifest carries a gradeable patch — grade it instead of failing closed");
        grader.PatchCalls[0].RepositoryId.ShouldBe(repoId);
        grader.PatchCalls[0].BaseSha.ShouldBe("deadbeef");
        grader.PatchCalls[0].Command.ShouldBe(Check);

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().AcceptancePassed.ShouldBe(true);
    }

    [Fact]
    public async Task A_graded_unit_also_captures_its_baseline_health_at_its_recorded_base()
    {
        // S3: the SAME oracle, run against the unit's recorded BASE (the S1 pin) with no candidate work — so a
        // differential consumer can tell "the candidate broke it" from "it was already broken".
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, producedBranch: "codespace/agent/one")));
        await SeedManifestAsync(teamId, agentId, repoId, branch: "codespace/agent/one", baseSha: "deadbeef", patchArtifactId: null);

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" }) { BaseGrade = new BenchmarkGrade { Passed = false, Detail = "tests-failed-exit-1" } };
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.BaseCalls.Count.ShouldBe(1, "the baseline is measured once, beside the candidate grade");
        grader.BaseCalls[0].BaseSha.ShouldBe("deadbeef");
        grader.BaseCalls[0].Command.ShouldBe(Check, "the SAME oracle — a different command would measure a different contract");

        var unit = SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson).Single();
        unit.AcceptancePassed.ShouldBe(true);
        unit.BaselinePassed.ShouldBe(false, "the base already failed this check — the candidate's pass is a FIX, and the differential can now credit it");
        unit.BaselineDetail.ShouldBe("tests-failed-exit-1");
    }

    [Fact]
    public async Task Parallel_units_off_the_same_base_share_one_baseline_measurement()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check), ("s2", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1","s2"]}""", SpawnOutcome(Unit(agentA, producedBranch: "codespace/agent/a"), Unit(agentB, producedBranch: "codespace/agent/b")));
        await SeedManifestAsync(teamId, agentA, repoId, branch: "codespace/agent/a", baseSha: "deadbeef", patchArtifactId: null);
        await SeedManifestAsync(teamId, agentB, repoId, branch: "codespace/agent/b", baseSha: "deadbeef", patchArtifactId: null);

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.BaseCalls.Count.ShouldBe(1, "both units started from the SAME immutable base under the SAME oracle — one measurement serves both");
        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .ShouldAllBe(r => r.BaselinePassed == true, "the shared measurement folds onto every unit");
    }

    [Fact]
    public async Task A_crashing_baseline_never_strands_the_candidate_grade()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, producedBranch: "codespace/agent/one")));
        await SeedManifestAsync(teamId, agentId, repoId, branch: "codespace/agent/one", baseSha: "deadbeef", patchArtifactId: null);

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" }) { ThrowOnBase = new InvalidOperationException("baseline exploded") };
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        var unit = SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson).Single();
        unit.AcceptancePassed.ShouldBe(true, "the candidate verdict must never be stranded by its own baseline's failure");
        unit.BaselinePassed.ShouldBeNull("an exploded capture records NO baseline — fail-soft, never fail-closed onto the unit");
    }

    [Fact]
    public async Task An_infra_failed_candidate_grade_skips_the_baseline_entirely()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, producedBranch: "codespace/agent/one")));
        await SeedManifestAsync(teamId, agentId, repoId, branch: "codespace/agent/one", baseSha: "deadbeef", patchArtifactId: null);

        // The candidate grade itself was INFRA (the clone never ran) — a second clone of the same unreachable repo
        // would waste a full grade budget for an unusable differential pair.
        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "clone-failed: fatal: could not read" });
        await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.BaseCalls.ShouldBeEmpty("an unmeasured candidate has nothing to differentiate — skip the baseline");
    }

    [Fact]
    public async Task Same_command_different_oracle_kind_never_shares_a_baseline()
    {
        // Scan M1: the memo key is the FULL spec identity — Kind routes a different oracle, so two subtasks sharing
        // an argv off the same base still measure different contracts.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentA = Guid.NewGuid();
        var agentB = Guid.NewGuid();

        var planPayload = JsonSerializer.Serialize(new
        {
            goal = Goal,
            subtasks = new object[]
            {
                new { id = "s1", title = "s1", instruction = "do s1", acceptance = new { command = Check } },
                new { id = "s2", title = "s2", instruction = "do s2", acceptance = new { command = Check, kind = "ArtifactPresent" } },
            },
        }, AgentJson.Options);
        await SeedPlanAsync(runId, teamId, sequence: 1, planPayload);
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1","s2"]}""", SpawnOutcome(Unit(agentA, producedBranch: "codespace/agent/a"), Unit(agentB, producedBranch: "codespace/agent/b")));
        await SeedManifestAsync(teamId, agentA, repoId, branch: "codespace/agent/a", baseSha: "deadbeef", patchArtifactId: null);
        await SeedManifestAsync(teamId, agentB, repoId, branch: "codespace/agent/b", baseSha: "deadbeef", patchArtifactId: null);

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.BaseCalls.Count.ShouldBe(2, "same base + same argv but a DIFFERENT oracle kind ⇒ two measurements, never a shared verdict");
    }

    [Fact]
    public async Task An_unbaselined_units_persisted_outcome_omits_the_baseline_keys_byte_identically()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, producedBranch: "codespace/agent/one")));
        // no manifest ⇒ no baseline

        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" }));

        var outcome = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson!;
        outcome.ShouldNotContain("baselinePassed", customMessage: "null-omitted means OMITTED — a spurious null key would break the folds' byte-identity contract on every ungraded unit");
        outcome.ShouldNotContain("baselineDetail");
    }

    [Fact]
    public async Task A_unit_without_a_recorded_base_folds_no_baseline()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();
        var agentId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentId, producedBranch: "codespace/agent/one")));
        // no manifest at all → no recorded base → nothing to measure

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.BaseCalls.ShouldBeEmpty("no recorded base ⇒ no baseline clone — never a guess");
        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .Single().BaselinePassed.ShouldBeNull("null-omitted — an unmeasured baseline is absent, never false");
    }

    [Fact]
    public async Task A_unit_with_no_branch_no_patch_and_expects_changes_false_is_a_vacuous_pass()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayloadWithExpectsChanges("s1", Check, expectsChanges: false));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), producedBranch: null)));
        // No manifest seeded at all — the producer genuinely made no changes, matching a real investigate-only subtask.

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(0);
        grader.PatchCalls.Count.ShouldBe(0);

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var unit = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single();
        unit.AcceptancePassed.ShouldBe(true, "the subtask declared no changes were expected, and none were produced — the correctly-predicted outcome, never a failure");
        unit.AcceptanceDetail.ShouldStartWith("not-applicable");
    }

    // ─── C2: a REPO-LESS unit is graded against what it captured, not failed closed on "no repo" ──────────

    /// <summary>
    /// The defect C2 closes, end to end through the real fold: a research/report subtask with NO repository and an
    /// <c>ArtifactPresent</c> oracle used to be handed <c>no-branch-or-repo</c> unconditionally. With no work
    /// present that detail classifies GENUINE — so the decider was told "RETRY this exact subtask" about a report it
    /// had already written correctly, forever. The fold now grades it against the deliverables the unit durably
    /// captured, and the prompt is asserted directly so the refutation evidence cannot survive this test.
    /// </summary>
    [Fact]
    public async Task A_repo_less_unit_is_graded_against_its_captured_deliverables_and_the_brain_is_never_told_to_retry()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var agentRunId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, ArtifactPlanPayload("s1", new[] { "report.md" }));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(agentRunId, producedBranch: null)));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, RepoLessGoalConfig(), grader);

        grader.CallCount.ShouldBe(0, "there is no branch to clone");
        grader.PatchCalls.Count.ShouldBe(0, "and no repo to apply a patch onto");

        var captured = grader.CapturedCalls.ShouldHaveSingleItem();
        captured.AgentRunId.ShouldBe(agentRunId, "the unit's OWN captured deliverables are the world — addressed by its attempt id");
        captured.Kind.ShouldBe(BenchmarkGradingKind.ArtifactPresent);
        captured.Command.ShouldBe(new[] { "report.md" });

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var unit = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single();
        unit.AcceptancePassed.ShouldBe(true, "the captured report satisfied its own oracle");
        unit.AcceptanceDetail.ShouldBe("artifact-present");
        unit.AcceptanceDetail.ShouldNotBe("no-branch-or-repo", "non-code work is now a first-class supervisor path OUTSIDE a repo too");

        var prompt = CodeSpace.Core.Services.Supervisor.Deciders.LlmSupervisorDecider.BuildUserPromptForTest(ctx);
        prompt.ShouldContain("acceptance PASSED", Case.Sensitive);
        prompt.ShouldNotContain("RETRY this exact subtask", Case.Sensitive,
            "the refutation evidence this PR must not leave behind: a repo-less unit with a captured, passing deliverable that the decider is still told to RETRY");
    }

    /// <summary>
    /// The category error stays fail-closed: a <c>TestsPass</c> argv has no code world to run in without a repo, so
    /// the captured-deliverable lane is never even consulted and the detail is byte-identical to before C2.
    /// </summary>
    /// <summary>
    /// An EMPTY rebuilt world has two causes that must never share a verdict. When the attempt's own capture health
    /// says the CAPTURE failed — a storage fault, or a walk that refused files it saw — the empty world is
    /// infrastructure: a retry re-bills an agent and fails identically forever. Only a clean capture that found
    /// nothing is GENUINE, because only then can another agent pass change the outcome.
    /// </summary>
    [Theory]
    [InlineData("fault", true, "the deliverable capture faulted")]
    [InlineData("refused", true, "the capture refused")]
    [InlineData("clean", false, null)]
    public async Task An_empty_rebuilt_world_is_infra_when_the_capture_itself_fell_short(string shape, bool infra, string? detailFragment)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var agentRunId = Guid.NewGuid();

        var unitJson = shape switch
        {
            "fault" => UnitWithCaptureHealth(agentRunId, captureFault: "IOException: the object store refused the write", refused: 0),
            "refused" => UnitWithCaptureHealth(agentRunId, captureFault: null, refused: 7),
            _ => UnitWithCaptureHealth(agentRunId, captureFault: null, refused: 0),
        };

        await SeedPlanAsync(runId, teamId, sequence: 1, ArtifactPlanPayload("s1", new[] { "report.md" }));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(unitJson));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "unused" })
        {
            CapturedGrade = new BenchmarkGrade { Passed = false, Detail = ISupervisorAcceptanceGrader.NoDeliverablesCaptured, Class = GradeFailureClass.Genuine },
        };

        var ctx = await RehydrateAsync(runId, teamId, RepoLessGoalConfig(), grader);

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var unit = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single();

        unit.AcceptancePassed.ShouldBe(false, "an empty world is never a pass either way");
        CodeSpace.Core.Services.Agents.AgentAcceptanceContract.IsInfraFailure(unit.AcceptanceDetail, workPresent: false).ShouldBe(infra);

        var prompt = CodeSpace.Core.Services.Supervisor.Deciders.LlmSupervisorDecider.BuildUserPromptForTest(ctx);

        if (infra)
        {
            unit.AcceptanceDetail.ShouldContain(detailFragment!, Case.Insensitive, "the refusal is NAMED — one unfalsifiable verdict must not replace another");
            prompt.ShouldNotContain("RETRY this exact subtask", Case.Sensitive, "a storage fault read as 'the model produced nothing' burns retries no retry can fix");
            prompt.ShouldContain("Do NOT retry the agent", Case.Sensitive);
        }
        else
        {
            unit.AcceptanceDetail.ShouldBe(ISupervisorAcceptanceGrader.NoDeliverablesCaptured, "a clean capture that found nothing is the honest GENUINE verdict");
            prompt.ShouldContain("RETRY this exact subtask", Case.Sensitive, "producing nothing IS what another agent pass can fix");
        }
    }

    [Fact]
    public async Task A_repo_less_tests_pass_unit_still_fails_closed_and_never_reaches_the_captured_lane()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), producedBranch: null)));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, RepoLessGoalConfig(), grader);

        grader.CapturedCalls.ShouldBeEmpty("a bare `exit 0` argv in a directory of captured documents would pass VACUOUSLY — that is a category error, not a verdict");

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var unit = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single();
        unit.AcceptancePassed.ShouldBe(false);
        unit.AcceptanceDetail.ShouldBe("no-branch-or-repo", "the exact detail this arm has always failed closed on");
    }

    [Fact]
    public async Task A_unit_with_no_branch_no_patch_and_expects_changes_true_fails_closed_exactly_like_the_default()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayloadWithExpectsChanges("s1", Check, expectsChanges: true));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), producedBranch: null)));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        var unit = SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single();
        unit.AcceptancePassed.ShouldBe(false);
        unit.AcceptanceDetail.ShouldBe("no-branch-or-repo");
    }

    [Fact]
    public async Task A_verb_inferred_read_only_subtask_with_no_branch_is_a_vacuous_pass_without_an_explicit_declaration()
    {
        // No explicit expectsChanges — the server infers it from the instruction's leading verb ("investigate").
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, InvestigatePlanPayload("s1", Check));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), producedBranch: null)));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().AcceptancePassed.ShouldBe(true, "'investigate' is a read-only verb — the server infers no changes were expected");
    }

    [Fact]
    public async Task A_subtask_authored_artifact_present_kind_is_forwarded_to_the_per_unit_grade()
    {
        // T1.1: a NON-coding subtask authors Kind=ArtifactPresent (its deliverable is a FILE, not a passing test) — the
        // per-unit fold must thread THAT oracle to the grade, not the default TestsPass. The stop-path kind-forwarding is
        // pinned in SupervisorAcceptanceFoldFlowTests; this pins the per-unit (per-subtask) path, so regressing the fold to
        // a hardcoded TestsPass is caught.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, ArtifactPlanPayload("s1", new[] { "docs/report.md" }));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "artifacts-present" });
        await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(1, "the unit's deliverable check runs once against its own branch");
        grader.LastCall!.Value.Command.ShouldBe(new[] { "docs/report.md" }, "the subtask's declared deliverable paths are the graded command");
        grader.LastCall.Value.Kind.ShouldBe(BenchmarkGradingKind.ArtifactPresent, "the per-unit fold forwards the subtask's authored oracle kind — not the default TestsPass");
    }

    [Fact]
    public async Task A_unit_without_a_contract_is_never_graded_and_is_byte_identical()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", null)));   // s1 carries NO acceptance
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));
        var before = await LedgerSpawnOutcomeAsync(runId, teamId);

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(0, "a subtask with no acceptance contract is never graded");
        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().AcceptancePassed.ShouldBeNull("no verdict is folded");
        (await LedgerSpawnOutcomeAsync(runId, teamId)).ShouldBe(before, "the durable spawn row is unchanged — no spurious UPDATE on the no-contract path");
    }

    [Fact]
    public async Task The_grade_runs_once_and_replay_reads_the_verdict_without_re_grading()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" }));
        var persistedAfterFirst = await LedgerSpawnOutcomeAsync(runId, teamId);

        // The SECOND rehydrate must NOT re-grade — the once-guard reads the folded verdict off the tape.
        var secondGrader = new RecordingGrader(new BenchmarkGrade { Passed = false, Detail = "WOULD-FLIP-IF-RE-GRADED" });
        var ctx2 = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), secondGrader);

        secondGrader.CallCount.ShouldBe(0, "replay reads the durable verdict — the grade I/O never re-runs (replay-deterministic)");
        ctx2.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson.ShouldBe(persistedAfterFirst, "the verdict is unchanged across replay");
        (await LedgerSpawnOutcomeAsync(runId, teamId)).ShouldBe(persistedAfterFirst, "no redundant UPDATE on replay");
    }

    [Fact]
    public async Task A_grader_failure_fails_closed_without_stranding_the_run()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), new RecordingGrader(new InvalidOperationException("sandbox exploded")));

        var spawn = ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn);
        SupervisorOutcome.ReadAgentResults(spawn.OutcomeJson).Single().AcceptancePassed.ShouldBe(false, "an unexpected grade failure folds not-accepted (fail-closed)");
    }

    [Fact]
    public async Task A_unit_with_no_branch_fails_closed_and_is_not_evidence()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        // The agent succeeded but pushed NO branch — there is nothing to grade against → fail-closed, not evidence.
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(Unit(Guid.NewGuid(), producedBranch: null)));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(0, "no branch to clone → the grader is never invoked");
        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .Single().AcceptancePassed.ShouldBe(false, "a contract-bearing unit with no gradeable branch fails closed");
    }

    [Fact]
    public async Task A_multi_repo_unit_is_graded_against_every_repo_it_changed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var webRepo = Guid.NewGuid();
        var apiRepo = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        // A unit whose agent ran MULTI-repo, changing BOTH web and api — its subtask's contract binds the WHOLE
        // change, so every repo it touched must be graded, not just its (legacy-compat) top-level ProducedBranch.
        var multiRepoUnit = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "web/x",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "web", RepositoryId = webRepo, ProducedBranch = "web/x", BaseBranch = "main", Access = WorkspaceAccess.Write },
                new RepositoryRunResult { Alias = "api", RepositoryId = apiRepo, ProducedBranch = "api/x", BaseBranch = "main", Access = WorkspaceAccess.Write },
            },
        };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(multiRepoUnit));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(2, "every repo the unit actually changed is graded against the same contract");
        grader.Calls.Select(c => (c.RepositoryId, c.Branch)).ShouldBe(new[] { (webRepo, "web/x"), (apiRepo, "api/x") }, "graded in authored order");
        grader.Calls.ShouldAllBe(c => c.Command.SequenceEqual(Check), "the SAME subtask contract binds every repo");

        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .Single().AcceptancePassed.ShouldBe(true, "all repos passed → the unit is accepted");
    }

    [Fact]
    public async Task A_multi_repo_unit_fails_closed_when_any_repo_fails_and_short_circuits()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var webRepo = Guid.NewGuid();
        var apiRepo = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        var multiRepoUnit = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "web/x",
            RepositoryResults = new[]
            {
                // web FAILS its check — the unit's contract is not satisfied even though api would have passed.
                new RepositoryRunResult { Alias = "web", RepositoryId = webRepo, ProducedBranch = "web/x", BaseBranch = "main", Access = WorkspaceAccess.Write },
                new RepositoryRunResult { Alias = "api", RepositoryId = apiRepo, ProducedBranch = "api/x", BaseBranch = "main", Access = WorkspaceAccess.Write },
            },
        };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(multiRepoUnit));

        var grader = new RecordingGrader(branch => new BenchmarkGrade { Passed = branch != "web/x", Detail = branch == "web/x" ? "tests-failed-exit-1" : "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(1, "the FIRST repo's failure short-circuits — api is never even cloned");
        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .Single().AcceptancePassed.ShouldBe(false, "ANY repo failing rejects the whole unit — a multi-repo change is all-or-nothing");
    }

    [Fact]
    public async Task A_multi_repo_failure_keeps_the_failing_repos_class_evidence_and_tail_under_the_repo_tag()
    {
        // P5-2: the failing repo's grade re-emits via `with` — before this, the aggregate REBUILT the grade and
        // silently dropped the typed Class, the CAS evidence id, AND the tail, leaving every multi-repo failure's
        // receipt evidence-less and its repair loop blind.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var evidenceId = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        var multiRepoUnit = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "web/x",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), ProducedBranch = "web/x", BaseBranch = "main", Access = WorkspaceAccess.Write },
                new RepositoryRunResult { Alias = "api", RepositoryId = Guid.NewGuid(), ProducedBranch = "api/x", BaseBranch = "main", Access = WorkspaceAccess.Write },
            },
        };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(multiRepoUnit));

        var grader = new RecordingGrader(branch => new BenchmarkGrade
        {
            Passed = false, Detail = "tests-failed-exit-1", Class = GradeFailureClass.Genuine,
            EvidenceArtifactId = evidenceId, EvidenceTail = "exit=1\nFAILED Web.Smoke",
        });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        var folded = SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson).Single();

        folded.AcceptanceDetail.ShouldBe("repo 'web': tests-failed-exit-1", "the repo tag still names the failing repo (classification sees through it)");
        folded.AcceptanceEvidenceId.ShouldBe(evidenceId, "the failing repo's CAS evidence binding survives the aggregate");
        folded.AcceptanceEvidenceTail.ShouldBe("exit=1\nFAILED Web.Smoke", "the failing repo's diagnosis survives the aggregate");
    }

    [Fact]
    public async Task A_multi_repo_unit_skips_a_repo_the_agent_never_changed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var webRepo = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        var multiRepoUnit = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = "web/x",
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "web", RepositoryId = webRepo, ProducedBranch = "web/x", BaseBranch = "main", Access = WorkspaceAccess.Write },
                // api was bound to the workspace but the agent made no changes there — nothing to verify.
                new RepositoryRunResult { Alias = "api", RepositoryId = Guid.NewGuid(), ProducedBranch = null, BaseBranch = "main", Access = WorkspaceAccess.Write },
            },
        };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(multiRepoUnit));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(1, "a repo with no produced branch is not a target — only 'web' is graded");
        grader.LastCall!.Value.RepositoryId.ShouldBe(webRepo);
        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .Single().AcceptancePassed.ShouldBe(true);
    }

    [Fact]
    public async Task A_multi_repo_unit_with_no_produced_branches_anywhere_fails_closed()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        // A contract-bearing multi-repo unit that pushed NOTHING anywhere — the multi-repo analogue of the
        // single-repo "no branch to grade against" floor: fail closed, never a silent accept.
        var multiRepoUnit = new SupervisorAgentResult
        {
            AgentRunId = Guid.NewGuid(), Status = "Succeeded", ProducedBranch = null,
            RepositoryResults = new[]
            {
                new RepositoryRunResult { Alias = "web", RepositoryId = Guid.NewGuid(), ProducedBranch = null, BaseBranch = "main", Access = WorkspaceAccess.Write },
                new RepositoryRunResult { Alias = "api", RepositoryId = Guid.NewGuid(), ProducedBranch = null, BaseBranch = "main", Access = WorkspaceAccess.Write },
            },
        };
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1"]}""", SpawnOutcome(multiRepoUnit));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "should-not-run" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(Guid.NewGuid()), grader);

        grader.CallCount.ShouldBe(0, "nothing to clone anywhere → the grader never runs");
        SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson)
            .Single().AcceptancePassed.ShouldBe(false, "a contract-bearing unit that produced no branch anywhere fails closed — never a silent accept");
    }

    [Fact]
    public async Task A_repo_overridden_unit_is_graded_against_its_own_repo_not_the_profile_default()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var profileRepo = Guid.NewGuid();
        var overrideRepo = Guid.NewGuid();

        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check)));
        // The model authored a per-agent dispatch overriding s1's repo — the unit must be graded against THAT repo (its
        // agent wrote there), not the profile default (which would be a false verdict).
        var spawnPayload = $$"""{"subtaskIds":["s1"],"agents":[{"subtaskId":"s1","repositoryId":"{{overrideRepo}}"}]}""";
        await SeedSpawnAsync(runId, teamId, sequence: 2, spawnPayload, SpawnOutcome(Unit(Guid.NewGuid(), "codespace/agent/s1")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        await RehydrateAsync(runId, teamId, GoalConfig(profileRepo), grader);

        grader.CallCount.ShouldBe(1);
        grader.LastCall!.Value.RepositoryId.ShouldBe(overrideRepo, "the repo-overridden unit is graded against the repo its agent actually wrote to");
    }

    [Fact]
    public async Task Only_the_contract_bearing_unit_in_a_mixed_wave_is_graded()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSupervisorRunAsync(teamId, userId);
        var repoId = Guid.NewGuid();

        // s1 carries a contract, s2 does not — only s1 is graded; the positional join maps results[0]↔s1, results[1]↔s2.
        await SeedPlanAsync(runId, teamId, sequence: 1, PlanPayload(("s1", Check), ("s2", null)));
        var s1Agent = Guid.NewGuid();
        var s2Agent = Guid.NewGuid();
        await SeedSpawnAsync(runId, teamId, sequence: 2, """{"subtaskIds":["s1","s2"]}""",
            SpawnOutcome(Unit(s1Agent, "codespace/agent/s1"), Unit(s2Agent, "codespace/agent/s2")));

        var grader = new RecordingGrader(new BenchmarkGrade { Passed = true, Detail = "tests-passed" });
        var ctx = await RehydrateAsync(runId, teamId, GoalConfig(repoId), grader);

        grader.CallCount.ShouldBe(1, "only the contract-bearing unit is graded");
        grader.LastCall!.Value.Branch.ShouldBe("codespace/agent/s1", "the positional join grades s1's own branch");

        var results = SupervisorOutcome.ReadAgentResults(ctx.PriorDecisions.Single(d => d.DecisionKind == SupervisorDecisionKinds.Spawn).OutcomeJson);
        results[0].AcceptancePassed.ShouldBe(true, "s1 (contract) folds a verdict");
        results[1].AcceptancePassed.ShouldBeNull("s2 (no contract) stays ungraded");
    }

    // ─── Helpers ───

    private static string PlanPayload(params (string Id, string[]? Command)[] subtasks)
    {
        var ordered = subtasks.Select(s => s.Command is null
            ? (object)new { id = s.Id, title = s.Id, instruction = "do " + s.Id }
            : new { id = s.Id, title = s.Id, instruction = "do " + s.Id, acceptance = new { command = s.Command } });

        return JsonSerializer.Serialize(new { goal = Goal, subtasks = ordered }, AgentJson.Options);
    }

    /// <summary>A plan whose single subtask authors a NON-coding acceptance — Kind=ArtifactPresent + the declared deliverable paths in the command slot.</summary>
    private static string ArtifactPlanPayload(string id, string[] paths) =>
        JsonSerializer.Serialize(new { goal = Goal, subtasks = new[] { new { id, title = id, instruction = "do " + id, acceptance = new { command = paths, kind = "ArtifactPresent" } } } }, AgentJson.Options);

    /// <summary>A single-subtask plan with an EXPLICIT S2 <c>expectsChanges</c> declaration alongside its acceptance command.</summary>
    private static string PlanPayloadWithExpectsChanges(string id, string[] command, bool expectsChanges) =>
        JsonSerializer.Serialize(new { goal = Goal, subtasks = new[] { new { id, title = id, instruction = "do " + id, acceptance = new { command }, expectsChanges } } }, AgentJson.Options);

    /// <summary>A single-subtask plan whose instruction opens with a read-only verb and carries NO explicit expectsChanges — exercises the server's verb-based S2 fallback.</summary>
    private static string InvestigatePlanPayload(string id, string[] command) =>
        JsonSerializer.Serialize(new { goal = Goal, subtasks = new[] { new { id, title = id, instruction = "investigate the root cause", acceptance = new { command } } } }, AgentJson.Options);

    /// <summary>Seed a manifest row directly (bypassing IPublishManifestStore, mirroring SeedDecisionAsync's direct-row style) — the durable per-unit source S2's patch fallback reads (I2), never re-derived from the decision's own outcome snapshot.</summary>
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

    private static SupervisorAgentResult Unit(Guid agentRunId, string? producedBranch) =>
        new() { AgentRunId = agentRunId, Status = "Succeeded", Summary = "did it", ProducedBranch = producedBranch };

    /// <summary>C2 — a repo-less unit carrying its attempt's CAPTURE health, the only thing that can tell a storage fault apart from an agent that produced nothing.</summary>
    private static SupervisorAgentResult UnitWithCaptureHealth(Guid agentRunId, string? captureFault, int refused) =>
        new() { AgentRunId = agentRunId, Status = "Succeeded", Summary = "wrote the findings report", DeliverableCaptureFault = captureFault, UncapturedDeliverables = refused };

    private static string SpawnOutcome(params SupervisorAgentResult[] units) =>
        JsonSerializer.Serialize(new { agentRunIds = units.Select(u => u.AgentRunId).ToArray(), agentCount = units.Length, agentResults = units }, AgentJson.Options);

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
            scope.Resolve<CodeSpace.Core.Services.Workflows.Lifecycle.IRunRecordLogger>(), scope.Resolve<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactOffloader>(), scope.Resolve<CodeSpace.Core.Services.Agents.Publish.IPublishManifestStore>(), scope.Resolve<CodeSpace.Core.Services.Supervisor.ISupervisorPublishedBranchResolver>(), scope.Resolve<CodeSpace.Core.Services.Completion.ICompletionAssessmentComposer>(), new AdmitAllBudgetLedger(),
        scope.Resolve<CodeSpace.Core.Services.Learning.ILessonReader>(),
        scope.Resolve<ILogger<SupervisorTurnService>>(),
        rubricJudge: null,
        modes: scope.Resolve<CodeSpace.Core.Services.Completion.IModeProfileRegistry>());

        return await service.RehydrateFromDecisionLogAsync(runId, teamId, NodeId, Goal, goalConfig, CancellationToken.None);
    }

    private async Task SeedPlanAsync(Guid runId, Guid teamId, int sequence, string payloadJson) =>
        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.Plan, payloadJson, "{}");

    private async Task SeedSpawnAsync(Guid runId, Guid teamId, int sequence, string payloadJson, string outcomeJson) =>
        await SeedDecisionAsync(runId, teamId, sequence, SupervisorDecisionKinds.Spawn, payloadJson, outcomeJson);

    private async Task SeedDecisionAsync(Guid runId, Guid teamId, int sequence, string kind, string payloadJson, string outcomeJson)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId, Sequence = sequence,
            DecisionKind = kind, IdempotencyKey = $"{kind}-{Guid.NewGuid():N}", InputHash = "test",
            Status = SupervisorDecisionStatus.Succeeded, PayloadJson = payloadJson, OutcomeJson = outcomeJson,
            FenceEpoch = 1, CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        await db.SaveChangesAsync();
    }

    private async Task<string?> LedgerSpawnOutcomeAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().SupervisorDecisionRecord.AsNoTracking()
            .Where(d => d.SupervisorRunId == runId && d.TeamId == teamId && d.DecisionKind == SupervisorDecisionKinds.Spawn)
            .Select(d => d.OutcomeJson)
            .SingleAsync();
    }

    private static SupervisorGoalConfig GoalConfig(Guid repoId) => new()
    {
        Goal = Goal,
        AgentProfile = new SupervisorAgentProfile { RepositoryId = repoId },
    };

    /// <summary>C2 — a goal with NO repository bound: the research/report shape whose units the fold must grade from what they captured.</summary>
    private static SupervisorGoalConfig RepoLessGoalConfig() => new()
    {
        Goal = Goal,
        AgentProfile = new SupervisorAgentProfile { RepositoryId = null },
    };

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
            Name = "sup-unit-accept-" + Guid.NewGuid().ToString("N")[..6],
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

    /// <summary>
    /// Records EVERY grade call (in order, with its full args) and returns a canned grade (constant, or per-branch via
    /// <paramref name="gradeByBranch"/>) — or throws (the unexpected-failure path). The one seam the per-unit fold
    /// grades at, faked so a test asserts call count/order + the exact (repo, branch, command) without real git. The
    /// per-branch mode is what a multi-repo test uses to make ONE repo fail while another passes.
    /// </summary>
    private sealed class RecordingGrader : ISupervisorAcceptanceGrader
    {
        private readonly BenchmarkGrade _grade;
        private readonly Func<string, BenchmarkGrade>? _gradeByBranch;
        private readonly Exception? _throw;

        public RecordingGrader(BenchmarkGrade grade) => _grade = grade;
        public RecordingGrader(Exception toThrow) { _throw = toThrow; _grade = new BenchmarkGrade { Passed = false, Detail = "unused" }; }
        public RecordingGrader(Func<string, BenchmarkGrade> gradeByBranch) { _gradeByBranch = gradeByBranch; _grade = new BenchmarkGrade { Passed = false, Detail = "unused" }; }

        public List<(Guid RepositoryId, Guid TeamId, string Branch, IReadOnlyList<string> Command, int TimeoutSeconds, BenchmarkGradingKind Kind)> Calls { get; } = new();
        public int CallCount => Calls.Count;
        public (Guid RepositoryId, Guid TeamId, string Branch, IReadOnlyList<string> Command, int TimeoutSeconds, BenchmarkGradingKind Kind)? LastCall => Calls.Count > 0 ? Calls[^1] : null;

        public List<(Guid RepositoryId, Guid TeamId, string BaseSha, Guid? PatchArtifactId, IReadOnlyList<string> Command)> PatchCalls { get; } = new();

        public Task<BenchmarkGrade> GradeAsync(Guid repositoryId, Guid teamId, string branch, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            Calls.Add((repositoryId, teamId, branch, spec.Command, timeoutSeconds, spec.Kind ?? BenchmarkGradingKind.TestsPass));
            if (_throw != null) throw _throw;
            return Task.FromResult(_gradeByBranch?.Invoke(branch) ?? _grade);
        }

        public Task<BenchmarkGrade> GradePatchAsync(Guid repositoryId, Guid teamId, string baseSha, string inlinePatch, Guid? patchArtifactId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            PatchCalls.Add((repositoryId, teamId, baseSha, patchArtifactId, spec.Command));
            if (_throw != null) throw _throw;
            return Task.FromResult(_grade);
        }

        public List<(Guid RepositoryId, string BaseSha, IReadOnlyList<string> Command)> BaseCalls { get; } = new();

        /// <summary>S3: the baseline grade — configurable so a differential test can make base and candidate disagree.</summary>
        public BenchmarkGrade BaseGrade { get; set; } = new() { Passed = true, Detail = "baseline-tests-passed" };

        /// <summary>When set, GradeBaseAsync throws — proves the candidate grade survives its own baseline's crash.</summary>
        public Exception? ThrowOnBase { get; set; }

        public Task<BenchmarkGrade> GradeBaseAsync(Guid repositoryId, Guid teamId, string baseSha, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            BaseCalls.Add((repositoryId, baseSha, spec.Command));
            if (ThrowOnBase != null) throw ThrowOnBase;
            return Task.FromResult(BaseGrade);
        }

        /// <summary>C2 — the repo-less lane: which units the fold asked to be graded against their CAPTURED deliverables.</summary>
        public List<(Guid AgentRunId, Guid TeamId, IReadOnlyList<string> Command, BenchmarkGradingKind Kind)> CapturedCalls { get; } = new();

        /// <summary>The verdict the rebuilt-world grade returns. Its default is the passing ArtifactPresent verdict a captured, correct report earns.</summary>
        public BenchmarkGrade CapturedGrade { get; set; } = new() { Passed = true, Detail = "artifact-present" };

        public Task<BenchmarkGrade> GradeCapturedAsync(Guid agentRunId, Guid teamId, SupervisorAcceptanceSpec spec, int timeoutSeconds, CancellationToken cancellationToken)
        {
            CapturedCalls.Add((agentRunId, teamId, spec.Command, spec.Kind ?? BenchmarkGradingKind.TestsPass));
            if (_throw != null) throw _throw;
            return Task.FromResult(CapturedGrade);
        }
    }
}

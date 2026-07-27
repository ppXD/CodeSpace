using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
using CodeSpace.Core.Services.Supervisor.Deciders;
using CodeSpace.Core.Services.Supervisor;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 Integration (real Postgres): the completion composer's first live chain (P2a-3) — a real tape (plan with
/// decision-bound ref → terminal spawn with a folded grade → terminal stop) + a durable requirement row compose
/// through adapter → write-through receipts → admission → operational selector → the pure reducer, and the
/// verdict lands honestly. Pins: the graded fold becomes a durable receipt EXACTLY-ONCE across re-composes; a
/// pre-protocol run projects LegacyUnknown and never re-derives; nothing here ever touches WorkflowRunStatus
/// (compute + record only — Lock Clause 1).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CompletionComposerFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionComposerFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_graded_tape_composes_to_an_honest_assessment_with_exactly_once_receipts()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Success);
        var planId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", acceptanceEvidenceId = (Guid?)EvidenceId, producedBranch = "codespace/agent/s1" } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Stop, "{}", "{}");
        await SeedManifestAsync(teamId, attemptId, baseSha: "b1", commitSha: "c1");

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();
        await store.UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = "acceptance:s1", Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
            new RequirementEnvelope { RequirementRef = "delivery:s1", Kind = ContractKinds.Delivery, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
            new RequirementEnvelope { RequirementRef = "output:s1", Kind = ContractKinds.Output, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);

        var composer = scope.Resolve<ICompletionAssessmentComposer>();

        var first = await composer.ComposeAsync(runId, teamId, CancellationToken.None);

        first.ShouldNotBeNull();
        first!.Mode.ShouldBe(CompletionEnforcementMode.Shadow);
        first.Assessment.Basis.ShouldBe(CompletionBasis.ContractDerived);
        first.Assessment.Verification.ShouldBe(VerificationDisposition.Failed, "the folded grade reached the reducer through the full chain");
        first.Assessment.Outcome.ShouldBe(OutcomeDisposition.Unsolved, "an engine-Success run with a FAILED oracle reads honestly Unsolved");
        first.Assessment.Execution.ShouldBe(ExecutionDisposition.Completed);
        first.Rejections.ShouldBeEmpty();
        first.ContractErrors.ShouldBeEmpty();

        // P3b-1: the staked delivery settled from the attempt's Pushed manifest — arrival is a fact, not prose.
        first.Assessment.Delivery.ShouldBe(DeliveryDisposition.Delivered);

        // P3b-3: the staked output settled from the manifest's produced-bytes hashes via the kernel's
        // hash-upgrade hook (verdict-less Unknown + ContentHashes -> Captured) — capture is a fact too.
        first.Assessment.Artifact.ShouldBe(ArtifactDisposition.Captured);

        var second = await composer.ComposeAsync(runId, teamId, CancellationToken.None);
        second!.Assessment.ShouldBe(first.Assessment, "same contract + same facts ⇒ same assessment");

        (await store.ListReceiptsAsync(runId, teamId, CancellationToken.None)).Count
            .ShouldBe(3, "one acceptance + one delivery + one output receipt, exactly-once — a re-compose lands on the first rows");

        (await ScopeRunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success, "compute + record ONLY — the composer never touches the terminal (Lock Clause 1)");

        // P3a-1: the fold's evidence id rode the bridge onto the receipt — EvidenceRef is a fact, not prose.
        var receipts = await store.ListReceiptsAsync(runId, teamId, CancellationToken.None);
        var acceptance = receipts.Single(r => r.Kind == ContractKinds.Acceptance);
        acceptance.EvidenceRef.ShouldBe(EvidenceId);

        // P3a-3: the verdict binds WHICH machinery judged and WHICH bytes it judged — labeled, never guessed.
        acceptance.EvaluatorVersion.ShouldBe(SupervisorAcceptanceGrader.EvaluatorVersion);
        acceptance.ContentHashes.ShouldBe(new[] { "base:b1", "candidate:c1" });

        // P3b-1: the delivery receipt names its evaluator (the publish pipeline), its target, and CAS evidence.
        var delivery = receipts.Single(r => r.Kind == ContractKinds.Delivery);
        delivery.EvaluatorVersion.ShouldBe(CompletionAssessmentComposer.DeliveryEvaluatorVersion);
        delivery.TargetRef.ShouldBe("primary");
        delivery.EvidenceRef.ShouldNotBeNull("the manifest snapshot is CAS evidence — a required delivery Passed without evidence would be capped at admission");
    }

    // ── P5-6: the MID-RUN "if you stopped now" hypothetical ───────────────────────────

    [Fact]
    public async Task A_running_run_composes_the_stopped_now_verdict_without_touching_anything_terminal()
    {
        // The same graded tape as the terminal test, but the run is still RUNNING and no stop decision exists —
        // the hypothetical synthesizes the clean-stop world (orderly terminal, no forced/give-up) and reads the
        // reducer's verdict on the facts so far. Write-throughs land the same exactly-once receipt rows a
        // terminal compose would, just earlier; the run row itself is never touched.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Running);
        var attemptId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{Guid.NewGuid()}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", producedBranch = "codespace/agent/s1" } } }));
        await SeedManifestAsync(teamId, attemptId, baseSha: "b1", commitSha: "c1");

        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();
        await store.UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = "acceptance:s1", Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
            new RequirementEnvelope { RequirementRef = "delivery:s1", Kind = ContractKinds.Delivery, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);

        var composer = scope.Resolve<ICompletionAssessmentComposer>();

        var whatIf = await composer.ComposeIfStoppedNowAsync(runId, teamId, CancellationToken.None);

        whatIf.ShouldNotBeNull("a contract-bearing post-F0 run has a reducer verdict to recite mid-run");
        whatIf!.Assessment.Verification.ShouldBe(VerificationDisposition.Failed, "the folded FAILED grade reaches the hypothetical through the full chain");
        whatIf.Assessment.Outcome.ShouldBe(OutcomeDisposition.Unsolved, "stopping now cannot read Solved over a failing oracle");
        whatIf.Assessment.Delivery.ShouldBe(DeliveryDisposition.Delivered, "the Pushed manifest already settled the staked delivery");
        whatIf.Assessment.Execution.ShouldBe(ExecutionDisposition.Completed, "the hypothetical IS a clean stop — the missing-stop degradation must not leak into the what-if");

        (await ScopeRunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Running, "the hypothetical composes and records receipts, never a terminal (Lock Clause 1)");

        // The bridge is exactly-once across hypothetical AND terminal composes — the mid-run rows are the rows.
        var receiptsAfterWhatIf = (await store.ListReceiptsAsync(runId, teamId, CancellationToken.None)).Count;
        await composer.ComposeAsync(runId, teamId, WorkflowRunStatus.Success, CancellationToken.None);
        (await store.ListReceiptsAsync(runId, teamId, CancellationToken.None)).Count
            .ShouldBe(receiptsAfterWhatIf, "a later terminal-boundary compose lands on the SAME rows the hypothetical wrote");
    }

    // ── The drift detector: the tape-only projection vs. the real DB-backed compose ────

    /// <summary>
    /// The bond behind <see cref="SupervisorTapeCompletion"/>. The two live-model gates have no database, so the
    /// decider's stopped-now block is composed there from the tape alone — and a projection that quietly disagrees
    /// with production would put the gates right back to scoring a prompt that does not ship, which is the defect it
    /// was built to remove. Same tape, same staking call, both paths, one recital string.
    /// </summary>
    [Fact]
    public async Task The_tape_only_projection_recites_exactly_what_the_real_composer_recites()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Running);
        var attemptId = Guid.NewGuid();

        var planPayload = """{"goal":"g","subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""";
        var spawnPayload = """{"subtaskIds":["s1"]}""";
        var spawnOutcome = JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", producedBranch = "codespace/agent/s1" } } });

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan, planPayload, $$"""{"planned":[],"count":1,"workPlanId":"{{Guid.NewGuid()}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn, spawnPayload, spawnOutcome);

        // The tape the gates would hold, in the shape they hold it.
        var tape = await LoadTapeAsync(runId, teamId);

        // Stake the way PRODUCTION stakes — the same helper the spawn executor calls over the same planned spec —
        // so the comparison is about the CHAIN, never about two people inventing different obligations.
        using var scope = _fixture.BeginScope();
        var store = scope.Resolve<ICompletionContractStore>();
        var planned = SupervisorOutcome.ReadPlanSubtasks(planPayload);
        await store.UpsertRequirementsAsync(runId, teamId,
            SupervisorUnitContract.BuildStakedRequirements(planned.Select(s => (s.Id, SupervisorUnitContract.Hash(s, null, null), SupervisorUnitContract.OwesDelivery(s))), ContractAuthority.ModelProposal),
            CancellationToken.None);

        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeIfStoppedNowAsync(runId, teamId, CancellationToken.None);

        var fromRows = SupervisorStopNowRecital.Render(composed?.Assessment);
        var fromTape = SupervisorStopNowRecital.Render(SupervisorTapeCompletion.ProjectIfStoppedNow(tape));

        fromRows.ShouldNotBeNull("the seeded run is contract-bearing, so production has a verdict to recite");
        fromTape.ShouldBe(fromRows, "the tape-only projection must recite the SAME block production recites — a divergence here is the live gates scoring a prompt that does not ship");
    }

    /// <summary>
    /// The documented boundary, asserted rather than trusted. A pushed manifest settles delivery for the DB compose
    /// and is invisible to the tape, so the two MAY differ here — but only in the conservative direction. A projection
    /// that read settled where production reads unresolved would tell a model its contract is met when it is not.
    /// </summary>
    [Fact]
    public async Task Where_the_projection_cannot_see_a_manifest_it_errs_toward_unresolved_never_toward_done()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Running);
        var attemptId = Guid.NewGuid();

        var planPayload = """{"goal":"g","subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""";

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan, planPayload, $$"""{"planned":[],"count":1,"workPlanId":"{{Guid.NewGuid()}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = true, acceptanceDetail = "tests-passed", producedBranch = "codespace/agent/s1" } } }));
        await SeedManifestAsync(teamId, attemptId, baseSha: "b1", commitSha: "c1");

        var tape = await LoadTapeAsync(runId, teamId);

        using var scope = _fixture.BeginScope();
        var planned = SupervisorOutcome.ReadPlanSubtasks(planPayload);
        await scope.Resolve<ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId,
            SupervisorUnitContract.BuildStakedRequirements(planned.Select(s => (s.Id, SupervisorUnitContract.Hash(s, null, null), SupervisorUnitContract.OwesDelivery(s))), ContractAuthority.ModelProposal),
            CancellationToken.None);

        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeIfStoppedNowAsync(runId, teamId, CancellationToken.None);
        var projected = SupervisorTapeCompletion.ProjectIfStoppedNow(tape);

        composed!.Assessment.Delivery.ShouldBe(DeliveryDisposition.Delivered, "the pushed manifest settles delivery for the row-reading compose");
        projected!.Delivery.ShouldNotBe(DeliveryDisposition.Delivered, "the tape carries no manifest, so the projection cannot claim delivery — and must not");

        SupervisorStopNowRecital.Render(projected).ShouldContain("UNRESOLVED", Case.Sensitive,
            "the safe direction: unseen evidence reads as owed, never as settled. A projection that recited an all-clear here would tell a model its contract is met when the tape cannot show it.");
    }

    /// <summary>
    /// The other direction of the same bond, and the one that is easy to get wrong: production stakes only under an
    /// AUTHORIZED plan, so a tape whose plan carries no ref has no obligations and no verdict. A projection that
    /// recited one anyway would invent a contract the run does not have — over-rendering, which is worse than the
    /// missing block it replaced, because the model would be told it owes something nobody staked.
    /// </summary>
    [Fact]
    public async Task Neither_path_recites_anything_for_a_plan_that_was_never_authorized()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Running);

        // A plan outcome with NO workPlanId — the pre-P1a shape the spawn executor refuses to stake against.
        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"goal":"g","subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""", """{"planned":[],"count":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn, """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = Guid.NewGuid(), status = "Succeeded", acceptancePassed = true, producedBranch = "codespace/agent/s1" } } }));

        var tape = await LoadTapeAsync(runId, teamId);

        using var scope = _fixture.BeginScope();
        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeIfStoppedNowAsync(runId, teamId, CancellationToken.None);

        SupervisorStopNowRecital.Render(composed?.Assessment).ShouldBeNull("nothing was staked, so production has no verdict to recite");
        SupervisorStopNowRecital.Render(SupervisorTapeCompletion.ProjectIfStoppedNow(tape)).ShouldBeNull("the projection must be silent wherever production is silent — an invented contract is the worse error");
    }

    /// <summary>The run's tape in the shape a decider holds it — the same read <c>SupervisorTurnService</c> rehydrates.</summary>
    private async Task<IReadOnlyList<SupervisorPriorDecision>> LoadTapeAsync(Guid runId, Guid teamId)
    {
        using var scope = _fixture.BeginScope();

        return await scope.Resolve<ISupervisorDecisionLog>().GetTerminalDecisionsAsync(runId, teamId, CancellationToken.None);
    }

    [Fact]
    public async Task A_contract_less_run_composes_no_stopped_now_verdict()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Running);

        using var scope = _fixture.BeginScope();

        (await scope.Resolve<ICompletionAssessmentComposer>().ComposeIfStoppedNowAsync(runId, teamId, CancellationToken.None))
            .ShouldBeNull("no staked requirements → nothing to recite, and the common contract-less run pays one indexed read");
    }

    [Fact]
    public async Task A_pre_protocol_run_composes_no_stopped_now_verdict()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: false, WorkflowRunStatus.Running);

        using var scope = _fixture.BeginScope();

        (await scope.Resolve<ICompletionAssessmentComposer>().ComposeIfStoppedNowAsync(runId, teamId, CancellationToken.None))
            .ShouldBeNull("a LegacyUnknown run has no contract-derived verdict — the recital never fabricates one");
    }

    [Fact]
    public async Task A_patch_only_manifest_settles_the_staked_delivery_as_policy_blocked()
    {
        // The patch exists but never ARRIVED — a definite non-arrival, never Delivered and never a silent hole.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Success);
        var planId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = true, acceptanceDetail = (string?)null, acceptanceEvidenceId = (Guid?)Guid.NewGuid(), producedBranch = (string?)null } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Stop, "{}", "{}");
        await SeedManifestAsync(teamId, attemptId, baseSha: "b1", commitSha: null, state: PublishState.PatchOnly);

        using var scope = _fixture.BeginScope();
        await scope.Resolve<ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = "delivery:s1", Kind = ContractKinds.Delivery, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
            new RequirementEnvelope { RequirementRef = "output:s1", Kind = ContractKinds.Output, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);

        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(runId, teamId, CancellationToken.None);

        composed.ShouldNotBeNull();
        composed!.Assessment.Delivery.ShouldBe(DeliveryDisposition.PolicyBlocked, "PatchOnly is a definite non-arrival — fail-close, whatever the acceptance verdict said");

        // P3b-3: the SAME manifest settles the two dimensions differently — the patch WAS captured (the work is
        // recoverable, nothing lost) even though it never arrived. Captured-but-parked, each fact on its own axis.
        composed.Assessment.Artifact.ShouldBe(ArtifactDisposition.Captured);

        // Outcome and Delivery are SEPARATE dimensions by design: this is the honest "solved but parked"
        // encoding (publish-or-park) — the clean-success predicate (Lock Clause 5) excludes PolicyBlocked
        // from VDS at the terminal, so a parked run counts as parked, never as a clean success.
        composed.Assessment.Outcome.ShouldBe(OutcomeDisposition.Solved);
    }

    private async Task SeedManifestAsync(Guid teamId, Guid agentRunId, string baseSha, string? commitSha, PublishState state = PublishState.Pushed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId,
            RepositoryAlias = "primary", Branch = state == PublishState.Pushed ? "codespace/agent/s1" : null,
            BaseSha = baseSha, CommitSha = commitSha, PatchArtifactId = state == PublishState.PatchOnly ? Guid.NewGuid() : null,
            PublishStateValue = state,
        });

        await db.SaveChangesAsync();
    }

    private static readonly Guid EvidenceId = Guid.NewGuid();

    [Fact]
    public async Task A_pre_protocol_run_projects_LegacyUnknown_and_derives_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: false, WorkflowRunStatus.Success);

        using var scope = _fixture.BeginScope();
        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(runId, teamId, CancellationToken.None);

        composed.ShouldNotBeNull();
        composed!.Mode.ShouldBe(CompletionEnforcementMode.Legacy);
        composed.Assessment.Basis.ShouldBe(CompletionBasis.LegacyUnknown);
        composed.Assessment.Outcome.ShouldBe(OutcomeDisposition.Unknown, "old tape is never re-derived into contract truth");

        (await scope.Resolve<ICompletionContractStore>().ListReceiptsAsync(runId, teamId, CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_non_terminal_run_composes_nothing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Running);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(runId, teamId, CancellationToken.None))
            .ShouldBeNull("an assessment is a terminal-time artifact");
    }

    [Fact]
    public async Task The_shadow_sweep_records_the_delta_append_only()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Success);
        var planId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = false, acceptanceDetail = "tests-failed-exit-1", producedBranch = "codespace/agent/s1" } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Stop, "{}", "{}");

        using var scope = _fixture.BeginScope();
        await scope.Resolve<ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = "acceptance:s1", Kind = ContractKinds.Acceptance, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);

        var shadow = scope.Resolve<ICompletionShadowService>();

        (await shadow.SweepAsync(batchSize: 50, CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        var db = scope.Resolve<CodeSpaceDbContext>();
        var record = await db.CompletionAssessmentRecord.AsNoTracking().SingleAsync(a => a.WorkflowRunId == runId);

        record.Outcome.ShouldBe("Unsolved");
        record.LegacyIsSolved.ShouldBeTrue("the legacy ladder read this engine-Success run Solved — THE degraded-inflation delta, now a standing row");
        record.WouldBeTerminalDecision.ShouldBe("HonestFailure", "P3b-4: the sealed six-state decision this run WOULD receive, recorded INACTIVE (Lock Clause 1) — an unsolved oracle is an honest failure, never inflated");
        record.EnforcementMode.ShouldBe("Shadow");
        record.Basis.ShouldBe("ContractDerived");

        // Re-sweep: the run has a record → not a candidate; even a direct re-record with an unchanged assessment appends nothing.
        (await shadow.SweepAsync(batchSize: 50, CancellationToken.None)).ShouldBe(0);
        (await db.CompletionAssessmentRecord.AsNoTracking().CountAsync(a => a.WorkflowRunId == runId)).ShouldBe(1);

        (await ScopeRunStatusAsync(runId)).ShouldBe(WorkflowRunStatus.Success, "Shadow NEVER mutates a terminal (Lock Clause 1)");
    }

    [Fact]
    public async Task An_abstaining_stop_composes_Abstained_and_would_be_NeedsClarification()
    {
        // P5-1 end-to-end: the model stopped WITH A QUESTION → the ledger reads Abstained (no objective claim in
        // either direction) and the sealed would-be terminal reads NeedsClarification — the ask returns to the
        // human, never a fake attempt and never a punished failure.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedTerminalRunAsync(teamId, userId, stampPolicy: true, WorkflowRunStatus.Failure);

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Stop, "{}",
            """{"outcome":"needs_clarification","summary":"Which auth provider should the login use?"}""");

        using var scope = _fixture.BeginScope();
        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(runId, teamId, CancellationToken.None);

        composed.ShouldNotBeNull();
        composed!.Assessment.Outcome.ShouldBe(OutcomeDisposition.Abstained);

        (await scope.Resolve<ICompletionShadowService>().SweepAsync(batchSize: 50, CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.CompletionAssessmentRecord.AsNoTracking().SingleAsync(a => a.WorkflowRunId == runId))
            .WouldBeTerminalDecision.ShouldBe("NeedsClarification");
    }

    // ── Seeds ──

    private async Task<Guid> SeedTerminalRunAsync(Guid teamId, Guid userId, bool stampPolicy, WorkflowRunStatus status)
    {
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Status = status;
        if (stampPolicy)
        {
            run.CompletionPolicyVersion = CompletionPolicy.CurrentVersion;
            run.CompletionEnforcementMode = CompletionPolicy.CurrentMode.ToString();
        }
        await db.SaveChangesAsync();
        return runId;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin);
        return await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
        {
            Name = "composer-" + Guid.NewGuid().ToString("N")[..8],
            Description = null,
            Definition = WorkflowsTestSeed.MinimalDefinition(),
            Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
            Enabled = true,
        });
    }

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

    private async Task<WorkflowRunStatus> ScopeRunStatusAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status;
    }
}

using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Completion;
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
/// 🟢 Integration (real Postgres): P2b-1 — the ONE production owner of the terminal SUCCESS claim
/// (<see cref="ICompletionTerminalAuthority"/>), arbitrating AT the terminal boundary (run still Running).
/// Pins: Legacy/Shadow and every non-Success claim pass through VERBATIM (Lock Clause 1's cohort gate);
/// an Enforced Success claim maps the sealed six-state decision onto the run vocabulary — honest failure
/// demotes with a named reason, the full predicate alone stays Success, and an unsettled obligation parks
/// (never a fake Success, never a fake Failure). Nothing here writes the run row — the engine's
/// CompleteRunAsync consumes the arbitration.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class CompletionTerminalAuthorityFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionTerminalAuthorityFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_shadow_run_and_a_non_success_claim_pass_through_verbatim()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Shadow");

        using var scope = _fixture.BeginScope();
        var authority = scope.Resolve<ICompletionTerminalAuthority>();

        var shadow = await authority.ArbitrateAsync(runId, teamId, "Shadow", WorkflowRunStatus.Success, CancellationToken.None);
        shadow.Status.ShouldBe(WorkflowRunStatus.Success);
        shadow.Decision.ShouldBeNull("only the Enforced cohort is arbitrated — Lock Clause 1's activation gate");

        var failure = await authority.ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Failure, CancellationToken.None);
        failure.Status.ShouldBe(WorkflowRunStatus.Failure);
        failure.Decision.ShouldBeNull("the engine's own Failure is already an honest non-success — the authority guards only the SUCCESS claim");
    }

    [Fact]
    public async Task An_enforced_success_claim_with_a_failed_oracle_demotes_to_honest_failure()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await SeedGradedTapeAsync(runId, teamId, acceptancePassed: false);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Failure, "an engine-Success run whose own oracle FAILED can never terminalize as Success under Enforced");
        arbitration.Decision.ShouldBe(TerminalDecision.HonestFailure);
        arbitration.Reason.ShouldNotBeNull();
        arbitration.Reason!.ShouldContain("honest failure");
    }

    [Fact]
    public async Task An_enforced_success_claim_with_the_full_predicate_stays_success()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        var attemptId = await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Decision.ShouldBe(TerminalDecision.CleanSuccess, "solved + verified + captured + delivered + reachable — the FULL predicate");
        arbitration.Status.ShouldBe(WorkflowRunStatus.Success);
        arbitration.Reason.ShouldBeNull();
    }

    [Fact]
    public async Task An_enforced_row_whose_mode_lost_enforceable_standing_parks_at_the_readiness_gate()
    {
        // Q3: the launch gate admitted this row while its cohort stood Enforceable; a later reviewed demotion
        // (modeled by restamping a registered mode that holds Shadow standing) must stop the cohort IMMEDIATELY —
        // the authority re-reads the registry every arbitration, so the row parks before any evidence composes.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await RestampProjectionKindAsync(runId, CodeSpace.Messages.Tasks.TaskProjectionKinds.SingleAgent);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Suspended);
        arbitration.Decision.ShouldBe(TerminalDecision.Unsupported);
        arbitration.Reason!.ShouldContain("ProtocolReadiness.Shadow", customMessage: "the park must name the standing that fell short — a demotion is legible, never a mystery park");
    }

    [Fact]
    public async Task An_enforced_success_claim_with_an_unsettled_obligation_parks()
    {
        // Acceptance passed but the staked delivery/output never settled (no manifest) — Unknown obligations
        // park the run for a human; never a fake Success, never a fake Failure.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Suspended);
        arbitration.Decision.ShouldBe(TerminalDecision.NeedsReview);
        arbitration.Reason!.ShouldContain("NeedsReview");
    }

    [Fact]
    public async Task A_read_only_unit_with_authorized_NA_stakes_reaches_clean_success()
    {
        // P2b-2 closes the read-only park hole: the declared no-changes unit stakes delivery/output as
        // ServerPolicy-AUTHORIZED-NotApplicable — explicitly authorized off, never silently absent — so under
        // Enforced it terminalizes CleanSuccess instead of parking on an Unknown artifact.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeNaAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeNaAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Decision.ShouldBe(TerminalDecision.CleanSuccess);
        arbitration.Status.ShouldBe(WorkflowRunStatus.Success);
    }

    // ── P4 per-cell law: a CleanSuccess missing a Required upstream stage parks naming the stage ──

    [Fact]
    public async Task An_enforced_success_claim_with_no_integration_evidence_parks_naming_the_stage()
    {
        // The FULL completion-side predicate holds (solved + verified + delivered + reachable) — but the tape is
        // plan → spawn → stop with NO merge: fresh spawned work nothing ever integrated. The supervisor profile
        // declares Integrate Required, so the stage gate refuses the CleanSuccess and names the exact cell —
        // fragmented per-agent delivery must never terminalize as integrated delivery.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        var attemptId = await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true, merged: false);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Suspended, "a supervisor-mode Success whose latest work was never integrated must park, never terminalize");
        arbitration.Decision.ShouldBe(TerminalDecision.Park);
        arbitration.Reason!.ShouldContain("Integrate", customMessage: "the park must name the exact missing stage");
        arbitration.Reason!.ShouldContain("mode 'supervisor'", customMessage: "…and the profile it was judged against");
    }

    [Fact]
    public async Task A_run_level_integration_manifest_satisfies_the_integrate_cell()
    {
        // The Integrate cell's SECOND ledger (P4, the plan-map lane's shape): the tape never merged — but a
        // git.integrate_run step recorded the run-level Integration candidate row, so the same claim that parks
        // in the merge-less test above now terminalizes CleanSuccess. Two ledgers, one cell, no double standard.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        var attemptId = await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true, merged: false);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await SeedIntegrationManifestAsync(teamId, runId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Decision.ShouldBe(TerminalDecision.CleanSuccess, "the pushed run-level candidate row evidences Integrate exactly as a tape merge does");
        arbitration.Status.ShouldBe(WorkflowRunStatus.Success);
    }

    [Fact]
    public async Task The_shadow_would_be_decision_mirrors_the_stage_gate()
    {
        // Same seeding as the stage park above, driven through the shadow sweep on a Shadow-mode run — parity
        // evidence recording "would have been CleanSuccess" for a run Enforced would park on a missing stage is
        // evidence about a rule that doesn't exist.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Shadow");
        var attemptId = await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true, merged: false);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);
        await FlipRunToAsync(runId, WorkflowRunStatus.Success);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<ICompletionShadowService>().SweepAsync(batchSize: 50, CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        var record = await scope.Resolve<CodeSpaceDbContext>().CompletionAssessmentRecord.AsNoTracking().SingleAsync(a => a.WorkflowRunId == runId);
        record.WouldBeTerminalDecision.ShouldBe(TerminalDecision.Park.ToString(), "the shadow's would-be decision must apply the authority's OWN stage gate, or the parity evidence lies about Enforced");
    }

    // ── P1 fail-close: a CleanSuccess built over integrity violations parks instead ──

    [Fact]
    public async Task An_enforced_success_claim_over_an_identity_less_receipt_parks()
    {
        // The FULL CleanSuccess predicate holds — and one identity-less receipt was folded into it under the
        // admission's Shadow tolerance. Lock Clause 3 names that Enforced-fatal; this is the refusal existing.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        var attemptId = await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);
        await AppendIdentitylessReceiptAsync(runId, teamId, "acceptance:s1");

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Suspended, "evidence with no WorkUnitRef cannot back an Enforced Success — park for a human, never a silent green");
        arbitration.Decision.ShouldBe(TerminalDecision.Park);
        arbitration.Reason!.ShouldContain("integrity violations");
        arbitration.Reason!.ShouldContain("WorkUnitRef");
    }

    [Fact]
    public async Task An_enforced_success_claim_over_an_unsupported_requirement_schema_parks()
    {
        // The fold itself is untouched (the reducer never reads the version) — the VOCABULARY is what cannot be
        // verified: an obligation staked in a schema this code does not speak can never back a Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        var attemptId = await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance, schemaVersion: "2038-draft");
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Suspended);
        arbitration.Decision.ShouldBe(TerminalDecision.Park);
        arbitration.Reason!.ShouldContain("2038-draft");
    }

    [Fact]
    public async Task A_failure_over_tainted_evidence_still_stamps_failure_never_park()
    {
        // Only the SUCCESS claim is gated: failure is already the conservative direction, and re-labeling an
        // honest failure as a park would hide the failure signal behind a review queue.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await SeedGradedTapeAsync(runId, teamId, acceptancePassed: false);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await AppendIdentitylessReceiptAsync(runId, teamId, "acceptance:s1");

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Status.ShouldBe(WorkflowRunStatus.Failure);
        arbitration.Decision.ShouldBe(TerminalDecision.HonestFailure);
    }

    [Fact]
    public async Task The_shadow_would_be_decision_mirrors_the_success_fail_close()
    {
        // Parity evidence exists to predict what Enforced WILL do — a recorded "would have been CleanSuccess" for
        // a run the authority would in fact refuse is evidence about a rule that doesn't exist. Same seeding as
        // the identity-less park above, driven through the shadow sweep instead of the authority.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Shadow");
        var attemptId = await SeedGradedTapeAsync(runId, teamId, acceptancePassed: true);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);
        await AppendIdentitylessReceiptAsync(runId, teamId, "acceptance:s1");
        await FlipRunToAsync(runId, WorkflowRunStatus.Success);

        using var scope = _fixture.BeginScope();
        (await scope.Resolve<ICompletionShadowService>().SweepAsync(batchSize: 50, CancellationToken.None)).ShouldBeGreaterThanOrEqualTo(1);

        var record = await scope.Resolve<CodeSpaceDbContext>().CompletionAssessmentRecord.AsNoTracking().SingleAsync(a => a.WorkflowRunId == runId);
        record.WouldBeTerminalDecision.ShouldBe(TerminalDecision.Park.ToString(), "the shadow's would-be decision must apply the authority's OWN fail-close, or the parity evidence lies about Enforced");
    }

    [Fact]
    public async Task Watermarks_bind_the_arbitration_to_the_ledgers_it_read()
    {
        // Lock Clause 2: the Enforced arbitration carries the ledgers' watermarks; a fact landing AFTER it
        // (a late receipt) flips the verify to false, so the engine recomposes or parks — never a stale stamp.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Enforced");
        await SeedGradedTapeAsync(runId, teamId, acceptancePassed: false);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);

        using var scope = _fixture.BeginScope();
        var authority = scope.Resolve<ICompletionTerminalAuthority>();

        var arbitration = await authority.ArbitrateAsync(runId, teamId, "Enforced", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Watermarks.ShouldNotBeNull("an Enforced arbitration binds what it read");
        (await authority.VerifyWatermarksAsync(runId, teamId, arbitration.Watermarks!, CancellationToken.None))
            .ShouldBeTrue("nothing moved — the terminal may stamp");

        await scope.Resolve<ICompletionContractStore>().AppendReceiptAsync(runId, teamId, new ReceiptEnvelope
        {
            RequirementRef = "acceptance:s1", Kind = ContractKinds.Acceptance, AttemptId = Guid.NewGuid(),
            Disposition = VerificationDisposition.Passed, Authority = ContractAuthority.ServerPolicy, ObservedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);

        (await authority.VerifyWatermarksAsync(runId, teamId, arbitration.Watermarks!, CancellationToken.None))
            .ShouldBeFalse("a late fact landed — the assessment no longer describes the ledgers");
    }

    [Fact]
    public async Task A_pass_through_arbitration_binds_no_watermarks()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunningRunAsync(teamId, userId, mode: "Shadow");

        using var scope = _fixture.BeginScope();
        var arbitration = await scope.Resolve<ICompletionTerminalAuthority>().ArbitrateAsync(runId, teamId, "Shadow", WorkflowRunStatus.Success, CancellationToken.None);

        arbitration.Watermarks.ShouldBeNull("pass-through claims nothing, so it binds nothing");
    }

    [Fact]
    public void The_engine_chokepoint_arbitrates_before_the_terminal_write()
    {
        // Lock Clause 1's architecture pin: CompleteRunAsync must consult the authority BEFORE any status write —
        // a new terminal writer (or a reorder that writes first) breaks this pin and must argue itself in review.
        var source = File.ReadAllText(Path.Combine(FindRepoRoot(), "backend/src/CodeSpace.Core/Services/Workflows/Engine/WorkflowEngine.cs"));
        var start = source.IndexOf("CompleteRunAsync(WorkflowRun", StringComparison.Ordinal);
        start.ShouldBeGreaterThanOrEqualTo(0, "the terminal chokepoint must remain present");
        var body = source[start..];

        body.IndexOf("ArbitrateAsync", StringComparison.Ordinal).ShouldBeGreaterThan(0);
        body.IndexOf("ArbitrateAsync", StringComparison.Ordinal).ShouldBeLessThan(body.IndexOf("run.Status =", StringComparison.Ordinal), "arbitration precedes the terminal write");
        body.IndexOf("VerifyWatermarksAsync", StringComparison.Ordinal).ShouldBeGreaterThan(0, "Lock Clause 2's re-verify is wired");
        body.IndexOf("VerifyWatermarksAsync", StringComparison.Ordinal).ShouldBeLessThan(body.IndexOf("run.Status =", StringComparison.Ordinal), "the watermark re-verify precedes the terminal write");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "backend"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
    }

    // ── Seeds (the composer flow tests' shapes, at the RUNNING boundary) ──

    private async Task<Guid> SeedRunningRunAsync(Guid teamId, Guid userId, string mode)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
            {
                Name = "authority-" + Guid.NewGuid().ToString("N")[..8],
                Description = null,
                Definition = WorkflowsTestSeed.MinimalDefinition(),
                Activations = new List<CodeSpace.Messages.Commands.Workflows.WorkflowActivationInput>(),
                Enabled = true,
            });
        }

        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var seed = _fixture.BeginScope();
        var db = seed.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Status = WorkflowRunStatus.Running;
        run.CompletionPolicyVersion = CompletionPolicy.CurrentVersion;
        run.CompletionEnforcementMode = mode;
        // P4: these fixtures simulate SUPERVISOR-shaped tapes on a minimal graph — stamp the mode honestly so the
        // authority's mode gate (unregistered ⇒ Unsupported park) reads the lane the tape actually models.
        run.ProjectionKind = CodeSpace.Messages.Tasks.TaskProjectionKinds.Supervisor;
        await db.SaveChangesAsync();
        return runId;
    }

    private async Task RestampProjectionKindAsync(Guid runId, string kind)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.ProjectionKind = kind;
        await db.SaveChangesAsync();
    }

    /// <summary>The canonical graded supervisor tape: plan → spawn → merge → stop. <paramref name="merged"/> false drops the merge decision — the exact tape P4's stage gate must refuse (fresh spawned work nothing ever integrated).</summary>
    private async Task<Guid> SeedGradedTapeAsync(Guid runId, Guid teamId, bool acceptancePassed, bool merged = true)
    {
        var attemptId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed, acceptanceDetail = acceptancePassed ? null : "tests-failed-exit-1", acceptanceEvidenceId = (Guid?)Guid.NewGuid(), producedBranch = "codespace/agent/s1" } } }));

        if (merged)
            await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Merge,
                """{"branches":["codespace/agent/s1"]}""",
                $$$"""{"integration":{"status":"integrated","integratedBranch":"codespace/integration/{{{runId:N}}}"}}""");

        await SeedDecisionAsync(runId, teamId, merged ? 4 : 3, SupervisorDecisionKinds.Stop, "{}", "{}");
        return attemptId;
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

    private async Task SeedManifestAsync(Guid teamId, Guid agentRunId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId, RepositoryId = repositoryId,
            RepositoryAlias = "primary", Branch = "codespace/agent/s1", BaseSha = "b1", CommitSha = "c1",
            PublishStateValue = PublishState.Pushed,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>The run-level Integration candidate row a <c>git.integrate_run</c> step records — the Integrate cell's second evidence ledger.</summary>
    private async Task SeedIntegrationManifestAsync(Guid teamId, Guid runId, Guid repositoryId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.PublishManifest.Add(new PublishManifest
        {
            Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Integration, WorkflowRunId = runId, RepositoryId = repositoryId,
            RepositoryAlias = "primary", Branch = $"codespace/integration/{runId:N}", BaseSha = "b1",
            PublishStateValue = PublishState.Pushed,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A live team-bound repository — the handoff probe's reachability target (an alias-only manifest fails CLOSED by design).</summary>
    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = CodeSpace.Messages.Enums.ProviderKind.GitLab, DisplayName = "instance",
            BaseUrl = $"https://git-{suffix}.local", OauthClientId = "client", OauthClientSecretEnc = "enc",
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instance.Id,
            ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = $"repo-{suffix}", FullPath = $"acme/repo-{suffix}",
            DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"https://git.local/acme/repo-{suffix}", Status = RepositoryStatus.Active,
        };

        db.ProviderInstance.Add(instance);
        db.Repository.Add(repo);
        await db.SaveChangesAsync();
        return repo.Id;
    }

    private async Task StakeNaAsync(Guid runId, Guid teamId, string requirementRef, string kind)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = requirementRef, Kind = kind, Requiredness = Requiredness.ServerPolicyAuthorizedNotApplicable, Authority = ContractAuthority.ServerPolicy, ContractSchemaVersion = "1" },
        }, CancellationToken.None);
    }

    private async Task StakeAsync(Guid runId, Guid teamId, string requirementRef, string kind, string schemaVersion = "1")
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = requirementRef, Kind = kind, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = schemaVersion },
        }, CancellationToken.None);
    }

    /// <summary>A Passed, EVIDENCED receipt with NO WorkUnitRef — the exact shape admission flags MissingIdentity yet folds under Shadow tolerance. Evidenced on purpose: an unevidenced pass would be capped at InfraUnknown and the scenario would stop being a CleanSuccess for reasons unrelated to identity.</summary>
    private async Task AppendIdentitylessReceiptAsync(Guid runId, Guid teamId, string requirementRef)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<ICompletionContractStore>().AppendReceiptAsync(runId, teamId, new ReceiptEnvelope
        {
            RequirementRef = requirementRef, Kind = ContractKinds.Acceptance, AttemptId = Guid.NewGuid(), WorkUnit = null,
            Disposition = VerificationDisposition.Passed, Authority = ContractAuthority.ServerPolicy, EvidenceRef = Guid.NewGuid(), ObservedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    private async Task FlipRunToAsync(Guid runId, WorkflowRunStatus status)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Status = status;
        run.CompletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
    }
}

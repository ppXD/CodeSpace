using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
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
/// 🟢 Integration (real Postgres): P4-U1 — the SINGLE-AGENT lane onto the completion spine. A contract-bearing
/// task dispatched outside the supervisor stakes the synthetic ROOT obligations at authorization (the same pure
/// builder the supervisor lane uses), and the composer projects the run as one settled attempt on the synthetic
/// root unit, bridging its own graded verdict + publish manifests into durable receipts. Pins: staking at
/// CreateAsync (and its supervisor-dispatch guard), the engine-Success + oracle-Failed run reading honestly
/// Unsolved on THIS lane, manifest-settled delivery/output, and exactly-once receipts across re-composes.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public sealed class SingleAgentSpineFlowTests
{
    private readonly PostgresFixture _fixture;

    public SingleAgentSpineFlowTests(PostgresFixture fixture) => _fixture = fixture;

    private static AgentTask Task_(SupervisorAcceptanceSpec? acceptance = null, WorkUnitRef? workUnit = null) => new()
    {
        Goal = "fix the parser",
        Harness = "codex-cli",
        Acceptance = acceptance ?? new SupervisorAcceptanceSpec { Command = new[] { "sh", "check.sh" } },
        WorkUnit = workUnit,
    };

    [Fact]
    public async Task A_contract_bearing_dispatch_stakes_the_root_obligations()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId, WorkflowRunStatus.Running);

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunService>().CreateAsync(Task_(), teamId, runId, nodeId: "agent1", cancellationToken: CancellationToken.None);

        var requirements = await scope.Resolve<ICompletionContractStore>().ListRequirementsAsync(runId, teamId, CancellationToken.None);

        requirements.Count.ShouldBe(3, "acceptance + delivery + output on the run's (node, iteration) unit, exactly like a supervisor unit");
        requirements.Select(r => r.RequirementRef).ShouldBe(new[] { "acceptance:agent1", "delivery:agent1", "output:agent1" }, ignoreOrder: true);
        requirements.ShouldAllBe(r => r.Requiredness == Requiredness.Required, "the default task expects changes — all three stages owed");
        requirements.ShouldAllBe(r => r.SpecHash!.StartsWith("sha256/canonical-json-v1:"));
    }

    [Fact]
    public async Task A_supervisor_dispatched_task_stakes_nothing_here()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId, WorkflowRunStatus.Running);
        var unit = new WorkUnitRef { WorkPlanId = Guid.NewGuid(), PlanVersion = 1, UnitId = "s1", ContractHash = "sha256/canonical-json-v1:aa" };

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IAgentRunService>().CreateAsync(Task_(workUnit: unit), teamId, runId, nodeId: "agent1", cancellationToken: CancellationToken.None);

        (await scope.Resolve<ICompletionContractStore>().ListRequirementsAsync(runId, teamId, CancellationToken.None))
            .ShouldBeEmpty("the supervisor stakes its own units at its own chokepoint — never twice");
    }

    [Fact]
    public async Task An_engine_success_run_with_a_failed_oracle_reads_honestly_unsolved_on_this_lane()
    {
        var (teamId, userId) = await SeedGradedSingleAgentRunAsync(acceptancePassed: false, withManifest: false);

        using var scope = _fixture.BeginScope();
        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(RunId, teamId, CancellationToken.None);

        composed.ShouldNotBeNull();
        composed!.Assessment.Basis.ShouldBe(CompletionBasis.ContractDerived);
        composed.Assessment.Verification.ShouldBe(VerificationDisposition.Failed, "the run's own graded verdict reached the reducer through the single-agent bridge");
        composed.Assessment.Outcome.ShouldBe(OutcomeDisposition.Unsolved, "engine Success with a FAILED oracle reads honestly Unsolved — the flagship pin, now on the single-agent lane");
        composed.ContractErrors.ShouldBeEmpty();

        var second = await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(RunId, teamId, CancellationToken.None);
        second!.Assessment.ShouldBe(composed.Assessment);

        (await scope.Resolve<ICompletionContractStore>().ListReceiptsAsync(RunId, teamId, CancellationToken.None))
            .Count(r => r.Kind == ContractKinds.Acceptance).ShouldBe(1, "exactly-once across re-composes");
    }

    [Fact]
    public async Task A_pushed_manifest_settles_delivery_and_output_on_this_lane()
    {
        var (teamId, userId) = await SeedGradedSingleAgentRunAsync(acceptancePassed: true, withManifest: true);

        using var scope = _fixture.BeginScope();
        var composed = await scope.Resolve<ICompletionAssessmentComposer>().ComposeAsync(RunId, teamId, CancellationToken.None);

        composed!.Assessment.Outcome.ShouldBe(OutcomeDisposition.Solved);
        composed.Assessment.Verification.ShouldBe(VerificationDisposition.Passed);
        composed.Assessment.Delivery.ShouldBe(DeliveryDisposition.Delivered, "the manifest bridge keys on the projected attempt — same machinery as the supervisor lane");
        composed.Assessment.Artifact.ShouldBe(ArtifactDisposition.Captured);
    }

    [Fact]
    public async Task A_map_fans_its_items_into_distinct_units_and_the_run_folds_worst_first()
    {
        // P4-U2 (L2): each map item is its OWN unit with its own staked obligations — one failed item honestly
        // fails the RUN's verification, however many siblings passed (the run-level oracle composition).
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedRunAsync(teamId, userId, WorkflowRunStatus.Running);

        Guid passId, failId;
        using (var scope = _fixture.BeginScope())
        {
            var service = scope.Resolve<IAgentRunService>();
            passId = (await service.CreateAsync(Task_(), teamId, runId, nodeId: "map1", iterationKey: "0", cancellationToken: CancellationToken.None)).Id;
            failId = (await service.CreateAsync(Task_(), teamId, runId, nodeId: "map1", iterationKey: "1", cancellationToken: CancellationToken.None)).Id;
        }

        using (var scope = _fixture.BeginScope())
        {
            var store = scope.Resolve<ICompletionContractStore>();
            var staked = await store.ListRequirementsAsync(runId, teamId, CancellationToken.None);
            staked.Count.ShouldBe(6, "two items x three stages — distinct units, never overwriting one root");
            staked.Count(r => r.RequirementRef.EndsWith("map1#0")).ShouldBe(3);
            staked.Count(r => r.RequirementRef.EndsWith("map1#1")).ShouldBe(3);
        }

        await GradeAsync(passId, passed: true);
        await GradeAsync(failId, passed: false);
        await MarkRunTerminalAsync(runId);

        using var verify = _fixture.BeginScope();
        var composed = await verify.Resolve<ICompletionAssessmentComposer>().ComposeAsync(runId, teamId, CancellationToken.None);

        composed!.Assessment.Verification.ShouldBe(VerificationDisposition.Failed, "worst-of across the map's units — 1-of-2 passing can never read verified");
        composed.Assessment.Outcome.ShouldBe(OutcomeDisposition.Unsolved);
        (await verify.Resolve<ICompletionContractStore>().ListReceiptsAsync(runId, teamId, CancellationToken.None))
            .Count(r => r.Kind == ContractKinds.Acceptance).ShouldBe(2, "one receipt per item, each on its own unit");
    }

    private async Task GradeAsync(Guid agentRunId, bool passed)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.AgentRun.SingleAsync(r => r.Id == agentRunId);
        run.Status = AgentRunStatus.Succeeded;
        run.ResultJson = JsonSerializer.Serialize(new AgentRunResult
        {
            Status = AgentRunStatus.Succeeded, ExitReason = "completed",
            AcceptancePassed = passed, AcceptanceDetail = passed ? "tests-passed" : "tests-failed-exit-1",
            AcceptanceEvidenceId = Guid.NewGuid(), ProducedBranch = "codespace/agent/x",
        }, AgentJson.Options);
        await db.SaveChangesAsync();
    }

    private async Task MarkRunTerminalAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.SingleAsync(r => r.Id == runId)).Status = WorkflowRunStatus.Success;
        await db.SaveChangesAsync();
    }

    // ── Seeds ──

    private Guid RunId;

    private async Task<(Guid TeamId, Guid UserId)> SeedGradedSingleAgentRunAsync(bool acceptancePassed, bool withManifest)
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        RunId = await SeedRunAsync(teamId, userId, WorkflowRunStatus.Running);

        Guid agentRunId;
        using (var scope = _fixture.BeginScope())
        {
            var run = await scope.Resolve<IAgentRunService>().CreateAsync(Task_(), teamId, RunId, nodeId: "agent1", cancellationToken: CancellationToken.None);
            agentRunId = run.Id;
        }

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            var agentRun = await db.AgentRun.SingleAsync(r => r.Id == agentRunId);
            agentRun.Status = AgentRunStatus.Succeeded;
            agentRun.ResultJson = JsonSerializer.Serialize(new AgentRunResult
            {
                Status = AgentRunStatus.Succeeded,
                ExitReason = "completed",
                AcceptancePassed = acceptancePassed,
                AcceptanceDetail = acceptancePassed ? "tests-passed" : "tests-failed-exit-1",
                AcceptanceEvidenceId = Guid.NewGuid(),
                ProducedBranch = "codespace/agent/root",
                PushedCommitSha = "c1",
            }, AgentJson.Options);

            if (withManifest)
                db.PublishManifest.Add(new PublishManifest
                {
                    Id = Guid.NewGuid(), TeamId = teamId, Kind = PublishManifestKind.Agent, AgentRunId = agentRunId,
                    RepositoryAlias = "primary", Branch = "codespace/agent/root", BaseSha = "b1", CommitSha = "c1",
                    PatchArtifactId = Guid.NewGuid(), PublishStateValue = PublishState.Pushed,
                });

            var run = await db.WorkflowRun.SingleAsync(r => r.Id == RunId);
            run.Status = WorkflowRunStatus.Success;

            await db.SaveChangesAsync();
        }

        return (teamId, userId);
    }

    private async Task<Guid> SeedRunAsync(Guid teamId, Guid userId, WorkflowRunStatus status)
    {
        Guid workflowId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, CodeSpace.Messages.Constants.Roles.Admin))
        {
            workflowId = await scope.Resolve<MediatR.IMediator>().Send(new CodeSpace.Messages.Commands.Workflows.CreateWorkflowCommand
            {
                Name = "single-" + Guid.NewGuid().ToString("N")[..8],
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
        run.Status = status;
        run.CompletionPolicyVersion = CompletionPolicy.CurrentVersion;
        run.CompletionEnforcementMode = CompletionPolicy.CurrentMode.ToString();
        await db.SaveChangesAsync();
        return runId;
    }
}

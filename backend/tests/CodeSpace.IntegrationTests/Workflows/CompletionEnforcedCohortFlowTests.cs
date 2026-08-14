using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.Core.Services.Workflows.Reconciliation;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Contracts;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using System.Text.Json;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// 🟢 High fidelity: the P2b Enforced-cohort canary through the REAL production chain — the definition's own
/// <see cref="WorkflowDefinition.CompletionMode"/> opt-in, the real <c>RunStarter</c>/<c>RunFromSnapshotStarter</c>
/// stamp, the real engine terminal, the real completion authority + composer over real Postgres. The fail-close
/// proof is the point: an Enforced run whose clean engine Success stakes NOTHING parks for a human instead of
/// terminalizing — the exact protocol the isolated canary cohort exists to exercise — while a definition without
/// the opt-in behaves byte-identically to before (Shadow pass-through), and an unreadable opt-in never stores.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CompletionEnforcedCohortFlowTests
{
    private readonly PostgresFixture _fixture;

    public CompletionEnforcedCohortFlowTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task An_enforced_definitions_unbacked_success_parks_instead_of_terminalizing()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionEnforcementMode.ShouldBe("Enforced", "the definition's opt-in must reach the run row through the real RunStarter");
        run.Status.ShouldBe(WorkflowRunStatus.Suspended, "an Enforced claim nothing qualified must park, never terminalize");
        run.Error.ShouldNotBeNull();
        run.Error.ShouldContain("completion-authority", customMessage: "the park must name its arbiter — check workflow_run.error for the decision detail");
        // P4: a bare trigger→terminal graph is the GENERIC mode — no registered conformance profile, so the
        // authority now parks it at the mode gate (before the zero-staked compose even runs), naming the mode.
        run.Error.ShouldContain("mode 'generic'", customMessage: "the park reason must name the unregistered operating mode");
    }

    [Fact]
    public async Task A_definition_without_the_opt_in_stays_shadow_and_passes_through()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(completionMode: null));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        using var scope = _fixture.BeginScope();
        var run = await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionEnforcementMode.ShouldBe("Shadow", "no opt-in inherits the platform default");
        run.Status.ShouldBe(WorkflowRunStatus.Success, "outside the cohort, behavior is byte-identical to before the flip existed");
    }

    [Fact]
    public async Task A_snapshot_run_of_an_enforced_definition_stamps_and_parks_too()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid runId;
        using (var scope = _fixture.BeginScope())
        {
            runId = await scope.Resolve<IRunFromSnapshotStarter>().StartFromSnapshotAsync(
                Definition(WorkflowDefinition.CompletionModeEnforced), teamId, userId,
                launchPayloadJson: null, scopeRepositoryIds: null, projectionKind: null, session: null, CancellationToken.None);
        }

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var run = await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);

        run.CompletionEnforcementMode.ShouldBe("Enforced", "the snapshot lane resolves the same opt-in from its frozen definition json");
        run.Status.ShouldBe(WorkflowRunStatus.Suspended);
    }

    [Fact]
    public async Task An_unknown_completion_mode_never_stores()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var ex = await Should.ThrowAsync<Exception>(() => CreateWorkflowAsync(teamId, userId, Definition("yolo")));

        ex.Message.ShouldContain("Unknown completionMode 'yolo'");
    }

    // ── P4: a completion park is DURABLE — the reconciler never re-drives it; Continue is the one channel ──

    [Fact]
    public async Task A_completion_park_is_durable_the_stranded_sweep_never_redrives_it()
    {
        // The parked run wears the stranded sweep's exact shape (Suspended, zero pending waits, past the grace
        // window) — without the park stamp the reconciler would re-dispatch it into a re-walk → re-arbitrate →
        // re-park churn loop forever, each cycle paying a full compose plus a live handoff probe.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);
        (await ReadRunAsync(runId)).CompletionParkedAt.ShouldNotBeNull("the terminal park must stamp its discriminator");

        await BackdatePastStrandedGraceAsync(runId);

        await ReconcileAsync();

        var run = await ReadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Suspended, "a completion park is deliberate — the stranded sweep must skip it, never re-drive it");
        run.CompletionParkedAt.ShouldNotBeNull("…and the park stamp must survive the sweep");
    }

    [Fact]
    public async Task A_continued_park_re_arbitrates_to_success_once_the_contract_is_answered()
    {
        // THE loop-closer the durable park exists for: park → a human fixes the contract world → Continue →
        // the replayed walk re-arbitrates against the then-current facts and terminalizes CleanSuccess. The runway:
        // a supervisor-stamped Enforced run parks (nothing staked, no tape); the operator's world-fix stakes the
        // full contract and lands the graded merged tape + a pushed manifest; Continue clears the stamp and the
        // re-driven engine stamps Success.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, Definition(WorkflowDefinition.CompletionModeEnforced));
        var runId = await RunManuallyAsync(teamId, userId, workflowId);
        await StampProjectionKindAsync(runId, CodeSpace.Messages.Tasks.TaskProjectionKinds.Supervisor);

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        var parked = await ReadRunAsync(runId);
        parked.Status.ShouldBe(WorkflowRunStatus.Suspended);
        parked.CompletionParkedAt.ShouldNotBeNull();

        var attemptId = await SeedGradedMergedTapeAsync(runId, teamId);
        var repositoryId = await SeedRepositoryAsync(teamId);
        await SeedManifestAsync(teamId, attemptId, repositoryId);
        await StakeAsync(runId, teamId, "acceptance:s1", ContractKinds.Acceptance);
        await StakeAsync(runId, teamId, "delivery:s1", ContractKinds.Delivery);
        await StakeAsync(runId, teamId, "output:s1", ContractKinds.Output);

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowService>().ContinueRunAsync(runId, teamId, CancellationToken.None))
                .ShouldBeTrue("a completion-parked run is exactly what the operator's Continue exists to re-arbitrate");

        (await ReadRunAsync(runId)).CompletionParkedAt.ShouldBeNull("Continue clears the stamp — a re-park must be a fresh decision, never a leftover");

        await ForceEnqueuedAsync(runId);
        await RunEngineAsync(runId);

        var run = await ReadRunAsync(runId);
        run.Status.ShouldBe(WorkflowRunStatus.Success, "the re-arbitration over the fixed contract world must terminalize CleanSuccess");
        run.CompletionParkedAt.ShouldBeNull();
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "enforced-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task<Guid> RunManuallyAsync(Guid teamId, Guid userId, Guid workflowId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new RunWorkflowManuallyCommand { WorkflowId = workflowId, Payload = null });
    }

    private async Task<WorkflowRun> ReadRunAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId);
    }

    private async Task BackdatePastStrandedGraceAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database.ExecuteSqlRawAsync(
            "UPDATE workflow_run SET last_modified_date = {0} WHERE id = {1}",
            DateTimeOffset.UtcNow - StuckRunReconcilerService.SuspendedStrandedAfter - TimeSpan.FromMinutes(5), runId);
    }

    private async Task ReconcileAsync()
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMediator>().Send(new ReconcileStuckRunsCommand());
    }

    private async Task StampProjectionKindAsync(Guid runId, string kind)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.ProjectionKind = kind;
        await db.SaveChangesAsync();
    }

    /// <summary>The canonical graded supervisor tape (plan → spawn(passed) → merge → stop) — the same shape <c>CompletionTerminalAuthorityFlowTests</c> seeds, landed AFTER the park as the human's world-fix.</summary>
    private async Task<Guid> SeedGradedMergedTapeAsync(Guid runId, Guid teamId)
    {
        var attemptId = Guid.NewGuid();
        var planId = Guid.NewGuid();

        await SeedDecisionAsync(runId, teamId, 1, SupervisorDecisionKinds.Plan,
            """{"subtasks":[{"id":"s1","title":"T","instruction":"fix it"}]}""",
            $$"""{"planned":[],"count":1,"workPlanId":"{{planId}}","workPlanVersion":1}""");
        await SeedDecisionAsync(runId, teamId, 2, SupervisorDecisionKinds.Spawn,
            """{"subtaskIds":["s1"]}""",
            JsonSerializer.Serialize(new { agentResults = new[] { new { agentRunId = attemptId, status = "Succeeded", acceptancePassed = true, acceptanceDetail = (string?)null, acceptanceEvidenceId = (Guid?)Guid.NewGuid(), producedBranch = "codespace/agent/s1" } } }));
        await SeedDecisionAsync(runId, teamId, 3, SupervisorDecisionKinds.Merge,
            """{"branches":["codespace/agent/s1"]}""",
            $$$"""{"integration":{"status":"integrated","integratedBranch":"codespace/integration/{{{runId:N}}}"}}""");
        await SeedDecisionAsync(runId, teamId, 4, SupervisorDecisionKinds.Stop, "{}", "{}");
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

    /// <summary>A live team-bound repository — the handoff probe's reachability target.</summary>
    private async Task<Guid> SeedRepositoryAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];

        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "instance",
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

    private async Task StakeAsync(Guid runId, Guid teamId, string requirementRef, string kind)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Completion.ICompletionContractStore>().UpsertRequirementsAsync(runId, teamId, new[]
        {
            new RequirementEnvelope { RequirementRef = requirementRef, Kind = kind, Requiredness = Requiredness.Required, Authority = ContractAuthority.ModelProposal, ContractSchemaVersion = "1" },
        }, CancellationToken.None);
    }

    /// <summary>Tests run the engine inline (no Hangfire worker), so the dispatcher's Pending→Enqueued CAS is mirrored directly — same discipline as <c>ErrorRoutingFlowTests.ReEnqueueAsync</c>.</summary>
    private async Task ForceEnqueuedAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<CodeSpaceDbContext>().Database
            .ExecuteSqlInterpolatedAsync($"UPDATE workflow_run SET status = 'Enqueued' WHERE id = {runId}");
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<Core.Services.Workflows.Engine.IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    // start → end: the smallest legal graph — the run's clean Success stakes nothing, which is exactly the claim
    // the authority must refuse to terminalize for the Enforced cohort.
    private static WorkflowDefinition Definition(string? completionMode) => new()
    {
        SchemaVersion = 1,
        CompletionMode = completionMode,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };
}

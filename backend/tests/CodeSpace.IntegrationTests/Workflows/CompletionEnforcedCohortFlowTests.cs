using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.RunSources;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Authorization;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

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
        run.Status.ShouldBe(WorkflowRunStatus.Suspended, "a clean engine Success that staked no obligation is an unbackable claim — the authority parks it for a human, never a fake Success");
        run.Error.ShouldNotBeNull();
        run.Error.ShouldContain("completion-authority", customMessage: "the park must name its arbiter — check workflow_run.error for the decision detail");
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

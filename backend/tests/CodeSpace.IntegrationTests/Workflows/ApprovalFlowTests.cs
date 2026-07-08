using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Engine v2 Phase 1.2 — human-in-the-loop approval. <c>flow.wait_approval</c> parks the run on
/// an Approval wait (no timer); a person POSTs a decision (the <c>ResumeRunCommand</c> chain),
/// which resolves the wait + resumes; the node surfaces the decision as <c>{ approved, comment,
/// by }</c> outputs. Exercised through the real command → handler → service so tenancy +
/// approval-only gating are covered too.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ApprovalFlowTests
{
    private readonly PostgresFixture _fixture;

    public ApprovalFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Approve_resumes_the_run_and_surfaces_the_decision_as_outputs()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ApprovalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Approval);
            wait.WakeAt.ShouldBeNull("an approval wait has no timer — it wakes only on the human decision");
        }

        var resumed = await ApproveAsync(runId, teamId, userId, approved: true, comment: "lgtm");
        resumed.ShouldBeTrue();

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "approval");
            node.Status.ShouldBe(NodeStatus.Success);
            var outputs = JsonDocument.Parse(node.OutputsJson).RootElement;
            outputs.GetProperty("approved").GetBoolean().ShouldBeTrue();
            outputs.GetProperty("comment").GetString().ShouldBe("lgtm");
            outputs.GetProperty("by").GetString().ShouldBe(userId.ToString());
        }
    }

    [Fact]
    public async Task Reject_resumes_with_approved_false()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ApprovalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);
        (await ApproveAsync(runId, teamId, userId, approved: false, comment: "no")).ShouldBeTrue();
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "approval");
        JsonDocument.Parse(node.OutputsJson).RootElement.GetProperty("approved").GetBoolean()
            .ShouldBeFalse("a reject surfaces approved=false; downstream branches on it via logic.if");
    }

    [Fact]
    public async Task Approving_a_run_in_another_team_is_not_found()
    {
        var (teamA, userA) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamA, userA, ApprovalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamA);
        await RunEngineAsync(runId);

        var (teamB, userB) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        using var scope = _fixture.BeginScopeAs(userB, teamB, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        await Should.ThrowAsync<KeyNotFoundException>(async () =>
            await mediator.Send(new ResumeRunCommand { RunId = runId, Approved = true }));
    }

    [Fact]
    public async Task Approving_a_run_without_a_pending_approval_returns_false()
    {
        // A run that already completed (no pending approval wait) — approving it is a no-op.
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId); // runs straight to Success — never suspends

        (await ApproveAsync(runId, teamId, userId, approved: true, comment: null))
            .ShouldBeFalse("there is no pending approval wait to resolve");
    }

    [Fact]
    public async Task Run_detail_surfaces_the_pending_approval_wait_for_the_UI()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, ApprovalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await RunEngineAsync(runId);

        WorkflowRunDetail? detail;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            detail = await scope.Resolve<IMediator>().Send(new GetWorkflowRunQuery { RunId = runId });

        detail.ShouldNotBeNull();
        detail!.Status.ShouldBe(WorkflowRunStatus.Suspended);
        detail.PendingWait.ShouldNotBeNull("a run parked on an approval surfaces its wait so the UI can show approve/reject");
        detail.PendingWait!.Kind.ShouldBe(WorkflowWaitKinds.Approval);
        detail.PendingWait.NodeId.ShouldBe("approval");
        detail.PendingWait.WakeAt.ShouldBeNull("approval waits have no timer");
        detail.PendingWait.Payload.GetProperty("prompt").GetString().ShouldBe("Deploy to production?",
            "the node's prompt is surfaced so the approver sees the question");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<bool> ApproveAsync(Guid runId, Guid teamId, Guid userId, bool approved, string? comment)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new ResumeRunCommand { RunId = runId, Approved = approved, Comment = comment });
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "approval-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = definition,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static WorkflowDefinition ApprovalDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "approval", TypeKey = "flow.wait_approval",
                    Config = WorkflowsTestSeed.Json("""{"prompt":"Deploy to production?"}"""),
                    Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "approval" },
            new() { From = "approval", To = "end" },
        },
    };
}

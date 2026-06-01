using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
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
/// Engine v2 — the <c>Action</c> wait kind (closed-loop review request groundwork). A run parked
/// on an Action wait is woken by <see cref="IWorkflowResumeService.ResumeByActionTokenAsync"/>: the
/// structured, authenticated sibling of the callback resume. Inserting the wait also proves the
/// 0035 CHECK constraint admits <c>'Action'</c>. The full node round-trip (a node that produces the
/// suspend + consumes the resolved payload) lands with <c>flow.wait_action</c> in a later PR — here
/// we pin the resume primitive: kind-scoped lookup, the <c>{ action, by, comment }</c> payload, and
/// the idempotent Suspended→(re-dispatched) flip.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ActionResumeFlowTests
{
    private readonly PostgresFixture _fixture;

    public ActionResumeFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Action_token_resumes_the_run_and_stamps_the_structured_payload()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var token = Guid.NewGuid().ToString("N");
        await ParkOnWaitAsync(runId, WorkflowWaitKinds.Action, token);

        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IWorkflowResumeService>()
                .ResumeByActionTokenAsync(token, "approve", userId, "looks good", values: null, teamId, CancellationToken.None);

        resumed.ShouldBeTrue();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldNotBe(WorkflowRunStatus.Suspended, "the action resume flips the run out of Suspended and re-dispatches it");

        var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
        wait.Status.ShouldBe(WorkflowWaitStatuses.Resolved);

        var payload = JsonDocument.Parse(wait.PayloadJson!).RootElement;
        payload.GetProperty("action").GetString().ShouldBe("approve", "the clicked button key is surfaced as the node's `action` output");
        payload.GetProperty("by").GetString().ShouldBe(userId.ToString(), "the authenticated clicker is attributed so the write-back knows who decided");
        payload.GetProperty("comment").GetString().ShouldBe("looks good");
    }

    [Fact]
    public async Task Unknown_action_token_does_not_resume()
    {
        using var scope = _fixture.BeginScope();

        var resumed = await scope.Resolve<IWorkflowResumeService>()
            .ResumeByActionTokenAsync("deadbeefdeadbeefdeadbeefdeadbeef", "approve", Guid.NewGuid(), null, values: null, Guid.NewGuid(), CancellationToken.None);

        resumed.ShouldBeFalse("an unknown / already-used token matches no pending action wait → 404 at the controller");
    }

    [Fact]
    public async Task Action_resume_ignores_a_non_action_wait_sharing_the_token()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var token = Guid.NewGuid().ToString("N");
        await ParkOnWaitAsync(runId, WorkflowWaitKinds.Callback, token);

        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IWorkflowResumeService>()
                .ResumeByActionTokenAsync(token, "approve", userId, null, values: null, teamId, CancellationToken.None);

        resumed.ShouldBeFalse("the lookup is kind-scoped — an action resume must not hijack a Callback wait");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Suspended, "the run stays parked — the wrong-kind resume was a no-op");
        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status
            .ShouldBe(WorkflowWaitStatuses.Pending);
    }

    [Fact]
    public async Task Action_resume_resolves_only_the_matched_wait_leaving_siblings_pending()
    {
        // Two cards parked on ONE run (parallel reviewers). Resolving one must NOT touch the other —
        // each card carries its own decision. (Regression guard: ResumeCore used to resolve ALL waits.)
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await ParkOnWaitAsync(runId, WorkflowWaitKinds.Action, "tok-A", nodeId: "wait_a");
        await ParkOnWaitAsync(runId, WorkflowWaitKinds.Action, "tok-B", nodeId: "wait_b");

        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowResumeService>()
                .ResumeByActionTokenAsync("tok-A", "approve", userId, null, values: null, teamId, CancellationToken.None)).ShouldBeTrue();

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.NodeId == "wait_a")).Status
            .ShouldBe(WorkflowWaitStatuses.Resolved, "the clicked card's wait resolves");
        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.NodeId == "wait_b")).Status
            .ShouldBe(WorkflowWaitStatuses.Pending, "a sibling card on the same run keeps waiting for its OWN click");
    }

    [Fact]
    public async Task Action_resume_ignores_a_wait_whose_run_is_in_another_team()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        await ParkOnWaitAsync(runId, WorkflowWaitKinds.Action, "tok-X");

        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IWorkflowResumeService>()
                .ResumeByActionTokenAsync("tok-X", "approve", userId, null, values: null, Guid.NewGuid(), CancellationToken.None);

        resumed.ShouldBeFalse("a card may only resolve a wait whose run is in the caller's team (tenancy guard)");
        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status
            .ShouldBe(WorkflowWaitStatuses.Pending);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Park <paramref name="runId"/> on a Pending wait of the given kind/token/node (mirrors what SuspendNodeAsync writes).</summary>
    private async Task ParkOnWaitAsync(Guid runId, string waitKind, string token, string nodeId = "review_wait")
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Status = WorkflowRunStatus.Suspended;

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(),
            RunId = runId,
            NodeId = nodeId,
            IterationKey = string.Empty,
            WaitKind = waitKind,
            Token = token,
            Status = WorkflowWaitStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "action-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = ManualDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private static WorkflowDefinition ManualDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };
}

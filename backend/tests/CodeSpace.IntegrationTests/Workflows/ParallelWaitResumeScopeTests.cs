using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows;
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
/// Regression guard for the parallel-wait resume-collapse bug. A run parked on several pending waits
/// must resolve ONLY the wait the signal targets. Before the fix, a callback POST and the run-level
/// approve both resolved EVERY pending wait (<c>onlyWaitId: null</c>), so two parallel human/external
/// waits on one run collapsed into a single decision — one POST silently answered an unrelated sibling.
/// Real Postgres + the real <see cref="WorkflowResumeService"/> / <see cref="WorkflowService"/>; the
/// waits are seeded directly because the bug lived purely in resume-resolution scope, not wait creation.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ParallelWaitResumeScopeTests
{
    private readonly PostgresFixture _fixture;

    public ParallelWaitResumeScopeTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Callback_resume_resolves_only_its_own_wait_leaving_a_parallel_sibling_pending()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSuspendedRunAsync(teamId, userId);

        const string callbackToken = "cb-tok-only-mine";
        var callbackWaitId = await AddWaitAsync(runId, "cb", WorkflowWaitKinds.Callback, callbackToken);
        var approvalWaitId = await AddWaitAsync(runId, "appr", WorkflowWaitKinds.Approval, token: "");

        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IWorkflowResumeService>()
                .ResumeByCallbackTokenAsync(callbackToken, """{"ok":true}""", CancellationToken.None);

        resumed.ShouldBeTrue("the callback wait matched the token and the run flipped Suspended->Pending");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var callbackWait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == callbackWaitId);
        callbackWait.Status.ShouldBe(WorkflowWaitStatuses.Resolved, "the callback's own wait is resolved");
        JsonDocument.Parse(callbackWait.PayloadJson!).RootElement.GetProperty("ok").GetBoolean()
            .ShouldBeTrue("the callback wait carries the POSTed body as its payload");

        var approvalWait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == approvalWaitId);
        approvalWait.Status.ShouldBe(WorkflowWaitStatuses.Pending,
            "the PARALLEL approval wait is untouched — a callback POST must not collapse a sibling wait");
        approvalWait.PayloadJson.ShouldBeNull("and carries no borrowed decision");
    }

    [Fact]
    public async Task Approve_run_resolves_only_the_approval_wait_leaving_a_parallel_callback_pending()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var runId = await SeedSuspendedRunAsync(teamId, userId);

        var approvalWaitId = await AddWaitAsync(runId, "appr", WorkflowWaitKinds.Approval, token: "");
        var callbackWaitId = await AddWaitAsync(runId, "cb", WorkflowWaitKinds.Callback, token: "cb-untouched");

        bool resumed;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
            resumed = await scope.Resolve<IWorkflowService>()
                .ApproveRunAsync(runId, teamId, userId, approved: true, comment: "lgtm", CancellationToken.None);

        resumed.ShouldBeTrue("the approval wait resolved and the run flipped Suspended->Pending");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var approvalWait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == approvalWaitId);
        approvalWait.Status.ShouldBe(WorkflowWaitStatuses.Resolved, "the approval wait is resolved");
        JsonDocument.Parse(approvalWait.PayloadJson!).RootElement.GetProperty("approved").GetBoolean()
            .ShouldBeTrue("the approval wait carries the approver's own decision");

        var callbackWait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == callbackWaitId);
        callbackWait.Status.ShouldBe(WorkflowWaitStatuses.Pending,
            "the PARALLEL callback wait is untouched — approving a run must not collapse a sibling wait");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedSuspendedRunAsync(Guid teamId, Guid userId)
    {
        var workflowId = await CreateMinimalWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        await db.WorkflowRun.Where(r => r.Id == runId)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, WorkflowRunStatus.Suspended));
        return runId;
    }

    private async Task<Guid> AddWaitAsync(Guid runId, string nodeId, string waitKind, string token)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var waitId = Guid.NewGuid();
        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId,
            RunId = runId,
            NodeId = nodeId,
            WaitKind = waitKind,
            Token = token,
            Status = WorkflowWaitStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return waitId;
    }

    private async Task<Guid> CreateMinimalWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "wait-scope-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = new WorkflowDefinition
            {
                SchemaVersion = 1,
                Nodes = new List<NodeDefinition>
                {
                    new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                    new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
                },
                Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
            },
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }
}

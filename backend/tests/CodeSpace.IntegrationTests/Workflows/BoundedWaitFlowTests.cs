using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Bounded waits — a wait with a DEADLINE auto-resolves with a default payload if no one responds, so
/// a parked run never hangs forever. The engine schedules <see cref="IWorkflowResumeService.ResumeByDeadlineAsync"/>
/// at the deadline; here we pin that resume primitive deterministically (no wall-clock): it resolves
/// ONLY the matched wait with the timeout payload, and is a NO-OP once a human resolved first — so the
/// scheduled deadline job and a real response are mutually idempotent (whoever flips the wait first wins).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class BoundedWaitFlowTests
{
    private readonly PostgresFixture _fixture;

    public BoundedWaitFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Deadline_resumes_the_run_and_stamps_the_timeout_payload()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var waitId = await ParkOnWaitAsync(runId, Guid.NewGuid().ToString("N"));

        var timeoutPayload = JsonSerializer.Serialize(new { action = "reject", _timedOut = true });

        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IWorkflowResumeService>()
                .ResumeByDeadlineAsync(waitId, timeoutPayload, CancellationToken.None);

        resumed.ShouldBeTrue("the deadline fired on a still-pending wait → it resolves + re-dispatches the run");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldNotBe(WorkflowRunStatus.Suspended, "the deadline resume flips the run out of Suspended and re-dispatches it");

        var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == waitId);
        wait.Status.ShouldBe(WorkflowWaitStatuses.Resolved);

        JsonDocument.Parse(wait.PayloadJson!).RootElement.GetProperty("action").GetString()
            .ShouldBe("reject", "the run resumes with the node's default-on-timeout decision, surfaced as its `action`");
    }

    [Fact]
    public async Task Deadline_is_a_no_op_when_a_human_resolved_first()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        var waitId = await ParkOnWaitAsync(runId, Guid.NewGuid().ToString("N"));

        // A human resolved the wait before the deadline (simulated: flip it Resolved directly).
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            var wait = await db.WorkflowRunWait.SingleAsync(w => w.Id == waitId);
            wait.Status = WorkflowWaitStatuses.Resolved;
            await db.SaveChangesAsync();
        }

        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IWorkflowResumeService>()
                .ResumeByDeadlineAsync(waitId, JsonSerializer.Serialize(new { action = "reject", _timedOut = true }), CancellationToken.None);

        resumed.ShouldBeFalse("the wait is no longer pending — the late deadline is an idempotent no-op, not a second resolution");

        using var verify = _fixture.BeginScope();
        var wait2 = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.Id == waitId);
        wait2.PayloadJson.ShouldBeNull("the no-op must not overwrite the human's resolution with the timeout payload");
    }

    [Fact]
    public async Task Unknown_wait_id_does_not_resume()
    {
        using var scope = _fixture.BeginScope();

        var resumed = await scope.Resolve<IWorkflowResumeService>()
            .ResumeByDeadlineAsync(Guid.NewGuid(), JsonSerializer.Serialize(new { action = "x" }), CancellationToken.None);

        resumed.ShouldBeFalse("no wait matches the id → no-op");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>Park <paramref name="runId"/> on a Pending Action wait (mirrors what SuspendNodeAsync writes); returns the wait id.</summary>
    private async Task<Guid> ParkOnWaitAsync(Guid runId, string token)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.SingleAsync(r => r.Id == runId);
        run.Status = WorkflowRunStatus.Suspended;

        var waitId = Guid.NewGuid();
        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId,
            RunId = runId,
            NodeId = "review_wait",
            IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Action,
            Token = token,
            WakeAt = DateTimeOffset.UtcNow.AddMinutes(30),
            Status = WorkflowWaitStatuses.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();
        return waitId;
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "bounded-" + Guid.NewGuid().ToString("N")[..6],
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

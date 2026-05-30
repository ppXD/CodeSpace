using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
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
/// Engine v2 Phase 1.2c — external callback. <c>flow.wait_callback</c> parks the run on a
/// Callback wait (the engine mints a token); an external system POSTs to the tokened URL (the
/// anonymous <c>ResumeWorkflowCallbackCommand</c> the controller dispatches), which resolves the
/// wait with the posted body + resumes. The node surfaces the body as its <c>body</c> output.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class CallbackFlowTests
{
    private readonly PostgresFixture _fixture;

    public CallbackFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Callback_token_resumes_the_run_and_surfaces_the_posted_body()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId, CallbackDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        string token;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Callback);
            wait.WakeAt.ShouldBeNull("a callback wait has no timer — it wakes only on the external POST");
            token = wait.Token;
        }

        // The external POST — the command the anonymous callbacks controller dispatches.
        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IMediator>().Send(new ResumeWorkflowCallbackCommand
            {
                Token = token,
                Body = """{"status":"done","score":7}""",
            });
        resumed.ShouldBeTrue();

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "callback");
            var body = JsonDocument.Parse(node.OutputsJson).RootElement.GetProperty("body");
            body.GetProperty("status").GetString().ShouldBe("done", "the posted body is surfaced as the node's `body` output");
            body.GetProperty("score").GetInt32().ShouldBe(7);
        }
    }

    [Fact]
    public async Task Unknown_callback_token_does_not_resume()
    {
        using var scope = _fixture.BeginScope();
        var resumed = await scope.Resolve<IMediator>().Send(new ResumeWorkflowCallbackCommand
        {
            Token = "deadbeefdeadbeefdeadbeefdeadbeef",
            Body = "{}",
        });
        resumed.ShouldBeFalse("an unknown / already-used token matches no pending callback wait → 404 at the controller");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition definition)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();
        return await mediator.Send(new CreateWorkflowCommand
        {
            Name = "callback-" + Guid.NewGuid().ToString("N")[..6],
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

    private static WorkflowDefinition CallbackDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "callback", TypeKey = "flow.wait_callback", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "callback" },
            new() { From = "callback", To = "end" },
        },
    };
}

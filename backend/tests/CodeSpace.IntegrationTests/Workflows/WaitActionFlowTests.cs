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
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Workflows;

/// <summary>
/// Engine v2 — <c>flow.wait_action</c> end-to-end. The node parks on an Action wait keyed by the
/// SUPPLIED token (via <c>SuspensionToken.CorrelationToken</c>, not a minted Guid), and a button
/// click — <c>ResumeByActionTokenAsync</c> — resolves exactly that wait and surfaces the decision as
/// the node's outputs. This is the wait half of the closed-loop review request; the posting half
/// (<c>chat.post_message</c>, which mints + carries the same token on the card) follows.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WaitActionFlowTests
{
    private readonly PostgresFixture _fixture;

    public WaitActionFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Wait_action_parks_on_the_supplied_token_then_resumes_with_the_decision()
    {
        var (teamId, userId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var workflowId = await CreateWorkflowAsync(teamId, userId);
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);

            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId);
            wait.WaitKind.ShouldBe(WorkflowWaitKinds.Action);
            wait.Token.ShouldBe("card-token-1", "the node's CorrelationToken becomes the wait token verbatim — not a minted Guid");
            wait.WakeAt.ShouldBeNull("an action wait has no timer — it wakes only on the click");
        }

        bool resumed;
        using (var scope = _fixture.BeginScope())
            resumed = await scope.Resolve<IWorkflowResumeService>()
                .ResumeByActionTokenAsync("card-token-1", "approve", userId, "lgtm", teamId, CancellationToken.None);
        resumed.ShouldBeTrue();

        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "wait");
            var outputs = JsonDocument.Parse(node.OutputsJson).RootElement;
            outputs.GetProperty("action").GetString().ShouldBe("approve", "the clicked button surfaces as the node's `action` output");
            outputs.GetProperty("by").GetString().ShouldBe(userId.ToString());
            outputs.GetProperty("comment").GetString().ShouldBe("lgtm");
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "waitaction-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = WaitActionDefinition(),
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static WorkflowDefinition WaitActionDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new() { Id = "wait", TypeKey = "flow.wait_action", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "token": "card-token-1" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "wait" },
            new() { From = "wait", To = "end" },
        },
    };
}

using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Chat;
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
/// Engine v2 — <c>chat.post_message</c> end-to-end. A workflow posts an interactive card into a
/// conversation AS the CodeSpace bot; the node outputs the message id + the action token it put on
/// the card (to wire into <c>flow.wait_action</c>). This is the posting half of the closed-loop
/// review request — combined with PR4b's wait node, the whole loop is drivable from a workflow.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ChatPostMessageFlowTests
{
    private readonly PostgresFixture _fixture;

    public ChatPostMessageFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Posts_an_interactive_card_as_the_bot_and_outputs_the_message_id_and_token()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var slug = "chatnode-" + Guid.NewGuid().ToString("N")[..8];
        Guid channelId;
        using (var scope = _fixture.BeginScope())
            channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, default);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, PostCardDefinition(channelId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var node = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "post");
        var outputs = JsonDocument.Parse(node.OutputsJson).RootElement;
        var messageId = Guid.Parse(outputs.GetProperty("messageId").GetString()!);
        var token = outputs.GetProperty("token").GetString();
        token.ShouldNotBeNullOrEmpty("an interactive post outputs the action token to wire into flow.wait_action");

        var message = await db.Message.AsNoTracking().SingleAsync(m => m.Id == messageId);
        message.ConversationId.ShouldBe(channelId);
        message.InteractionJson.ShouldNotBeNull();
        message.InteractionJson!.ShouldContain("approve", customMessage: "the card's action buttons are persisted on the message");
        message.InteractionJson.ShouldContain(token!, customMessage: "the card's wait-target token must equal the node's token output");

        // Authored by the team bot (hidden by the global filter — bypass to inspect).
        var author = await db.User.AsNoTracking().IgnoreQueryFilters().SingleAsync(u => u.Id == message.AuthorUserId);
        author.IsBot.ShouldBeTrue("the workflow posts as the CodeSpace bot, never a human");
    }

    [Fact]
    public async Task With_wait_for_response_on_the_node_parks_itself_then_outputs_the_clicked_action()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var slug = "chatwait-" + Guid.NewGuid().ToString("N")[..8];
        Guid channelId;
        using (var scope = _fixture.BeginScope())
            channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, default);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, PostAndWaitDefinition(channelId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);

        // First pass: the post_message node posted the card AND parked — one node, no separate flow.wait_action.
        string token;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            var wait = await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Action);
            wait.NodeId.ShouldBe("post", "the post_message node ITSELF parked — there is no separate wait node");
            token = wait.Token;
        }

        // A click resolves exactly this card's wait; re-dispatching drives the node's resumed pass.
        using (var scope = _fixture.BeginScope())
            (await scope.Resolve<IWorkflowResumeService>()
                .ResumeByActionTokenAsync(token, "approve", ownerId, "lgtm", values: null, teamId, CancellationToken.None)).ShouldBe(ActionResumeResult.Resumed);

        await RunEngineAsync(runId);

        using var verify2 = _fixture.BeginScope();
        var db2 = verify2.Resolve<CodeSpaceDbContext>();
        (await db2.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

        var node = await db2.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "post");
        var outputs = JsonDocument.Parse(node.OutputsJson).RootElement;
        outputs.GetProperty("action").GetString().ShouldBe("approve", "the post_message node surfaces the clicked action as ITS OWN output — the downstream node uses it directly, no flow.wait_action needed");
        outputs.GetProperty("by").GetString().ShouldBe(ownerId.ToString());
        outputs.GetProperty("comment").GetString().ShouldBe("lgtm");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "postcard-" + Guid.NewGuid().ToString("N")[..6],
            Description = null,
            Definition = def,
            Activations = new List<WorkflowActivationInput>(),
            Enabled = true,
        });
    }

    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    private static WorkflowDefinition PostCardDefinition(Guid channelId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new()
            {
                Id = "post",
                TypeKey = "chat.post_message",
                Config = WorkflowsTestSeed.EmptyJson(),
                Inputs = WorkflowsTestSeed.Json($$"""
                    { "conversationId": "{{channelId}}", "body": "Review PR #9?",
                      "actions": [ {"key":"approve","label":"Approve","style":"Primary"}, {"key":"reject","label":"Reject","style":"Danger"} ] }
                    """),
            },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "post" },
            new() { From = "post", To = "end" },
        },
    };

    // Same shape as PostCardDefinition but the post node opts into waitForResponse — so it posts AND waits
    // in one node, surfacing the click as its own outputs (no separate flow.wait_action).
    private static WorkflowDefinition PostAndWaitDefinition(Guid channelId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new()
            {
                Id = "post",
                TypeKey = "chat.post_message",
                Config = WorkflowsTestSeed.Json("""{ "waitForResponse": true }"""),
                Inputs = WorkflowsTestSeed.Json($$"""
                    { "conversationId": "{{channelId}}", "body": "Review PR #9?",
                      "actions": [ {"key":"approve","label":"Approve"}, {"key":"reject","label":"Reject"} ] }
                    """),
            },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "post" },
            new() { From = "post", To = "end" },
        },
    };
}

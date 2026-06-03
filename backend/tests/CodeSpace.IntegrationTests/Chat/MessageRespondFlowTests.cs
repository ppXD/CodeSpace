using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Chat;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Commands.Chat;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// The closed loop, end-to-end: a workflow posts an interactive card (chat.post_message) and parks
/// on it (flow.wait_action); a person clicks a button via the real respond command (controller →
/// handler → MessageInteractionService); that resolves the parked wait (token re-derived server-side
/// from the message) and the run resumes with the decision. Plus the respond endpoint's guard rails.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MessageRespondFlowTests
{
    private readonly PostgresFixture _fixture;

    public MessageRespondFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Post_card_then_respond_resolves_the_wait_resumes_the_run_and_stamps_resolution()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, ReviewLoopDefinition(channelId, ownerId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);   // posts the card, parks on flow.wait_action

        Guid messageId;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            var post = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "post");
            messageId = Guid.Parse(JsonDocument.Parse(post.OutputsJson).RootElement.GetProperty("messageId").GetString()!);
        }

        // The owner clicks "Approve" with a comment — through the real respond command (token never leaves the server).
        await RespondViaMediatorAsync(ownerId, teamId, messageId, "approve", "looks good");

        await RunEngineAsync(runId);   // the wait resumes with the decision

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var wait = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "wait");
            var waitOut = JsonDocument.Parse(wait.OutputsJson).RootElement;
            waitOut.GetProperty("action").GetString().ShouldBe("approve", "the clicked button flows through to the wait node's outputs");
            waitOut.GetProperty("comment").GetString().ShouldBe("looks good", "the comment plumbs end-to-end through the real command → resume payload → node output");

            var message = await db.Message.AsNoTracking().SingleAsync(m => m.Id == messageId);
            var interaction = MessageInteractionJson.Deserialize(message.InteractionJson);
            interaction!.State.ShouldBe(InteractionState.Resolved, "the message's interaction is stamped resolved (display mirror)");
            interaction.Resolution!.ResponseKey.ShouldBe("approve");
            interaction.Resolution.ByUserId.ShouldBe(ownerId);
            interaction.Resolution.Comment.ShouldBe("looks good");
        }

        // Responding again is rejected — the interaction is closed.
        await Should.ThrowAsync<InvalidOperationException>(() => RespondViaMediatorAsync(ownerId, teamId, messageId, "approve"));
    }

    [Fact]
    public async Task Respond_guards_against_missing_plain_bad_key_and_non_member()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);

        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent { Buttons = new List<InteractionButton> { new() { Key = "approve", Label = "Approve" } } },
            Target = new WorkflowWaitTarget { Token = "tok-guard" },
            AllowedResponderUserIds = new[] { ownerId },
        };

        Guid cardId, plainId;
        using (var scope = _fixture.BeginScope())
        {
            var bot = scope.Resolve<IChatBotService>();
            cardId = (await bot.PostAsBotAsync(channelId, "Review?", card, default)).Id;
            plainId = (await bot.PostAsBotAsync(channelId, "fyi, deployed", interaction: null, default)).Id;
        }

        await RespondDirectShouldThrow<KeyNotFoundException>(teamId, Guid.NewGuid(), "approve", ownerId);   // message not found
        await RespondDirectShouldThrow<KeyNotFoundException>(teamId, plainId, "approve", ownerId);          // a plain message has nothing to respond to
        await RespondDirectShouldThrow<InvalidOperationException>(teamId, cardId, "bogus", ownerId);        // not a valid button key
        await RespondDirectShouldThrow<InvalidOperationException>(teamId, cardId, "approve", Guid.NewGuid()); // a non-member / non-allowed responder
    }

    [Fact]
    public async Task Respond_rejects_a_required_comment_button_without_a_comment()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);

        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent { Buttons = new List<InteractionButton> { new() { Key = "request_changes", Label = "Request changes", RequiresComment = true } } },
            Target = new WorkflowWaitTarget { Token = "tok-rc" },
            AllowedResponderUserIds = new[] { ownerId },
        };

        Guid cardId;
        using (var scope = _fixture.BeginScope())
            cardId = (await scope.Resolve<IChatBotService>().PostAsBotAsync(channelId, "Review?", card, default)).Id;

        // RespondDirectShouldThrow passes a null comment — a requires-comment button must reject that server-side.
        await RespondDirectShouldThrow<InvalidOperationException>(teamId, cardId, "request_changes", ownerId);
    }

    [Fact]
    public async Task Comments_accumulate_on_an_open_card_and_are_open_to_any_member_not_just_the_responder()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);

        // The allowed RESPONDER (the decider) is someone else — yet the owner, a conversation member, can
        // still COMMENT. Discussion is open to the conversation; only the decision is restricted.
        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent { Buttons = new List<InteractionButton> { new() { Key = "approve", Label = "Approve" } } },
            Target = new WorkflowWaitTarget { Token = "tok-comments" },
            AllowedResponderUserIds = new[] { Guid.NewGuid() },
        };

        Guid cardId;
        using (var scope = _fixture.BeginScope())
            cardId = (await scope.Resolve<IChatBotService>().PostAsBotAsync(channelId, "Review?", card, default)).Id;

        await RespondViaMediatorAsync(ownerId, teamId, cardId, MessageInteractionPolicy.CommentKey, "taking a look");
        await RespondViaMediatorAsync(ownerId, teamId, cardId, MessageInteractionPolicy.CommentKey, "one more thing");

        using var verify = _fixture.BeginScope();
        var interaction = MessageInteractionJson.Deserialize(
            (await verify.Resolve<CodeSpaceDbContext>().Message.AsNoTracking().SingleAsync(m => m.Id == cardId)).InteractionJson)!;

        interaction.State.ShouldBe(InteractionState.Open, "comments never resolve — the card stays a living thread");
        interaction.Resolution.ShouldBeNull();
        interaction.Responses.Count.ShouldBe(2, "every comment accumulates, repeatably, in order");
        interaction.Responses.ShouldAllBe(r => r.Kind == InteractionResponseKind.Comment && r.ByUserId == ownerId);
        interaction.Responses[0].Comment.ShouldBe("taking a look");
        interaction.Responses[1].Comment.ShouldBe("one more thing");
    }

    [Fact]
    public async Task Comment_guards_reject_empty_text_and_a_non_member()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);

        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent { Buttons = new List<InteractionButton> { new() { Key = "approve", Label = "Approve" } } },
            Target = new WorkflowWaitTarget { Token = "tok-c-guard" },
        };
        Guid cardId;
        using (var scope = _fixture.BeginScope())
            cardId = (await scope.Resolve<IChatBotService>().PostAsBotAsync(channelId, "Review?", card, default)).Id;

        await RespondDirectCommentShouldThrow(teamId, cardId, ownerId, "   ");                    // a member, but empty text
        await RespondDirectCommentShouldThrow(teamId, cardId, Guid.NewGuid(), "I'm not in here");  // text, but not a member
    }

    [Fact]
    public async Task Discussion_then_a_decision_is_one_ordered_timeline_then_the_card_closes()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, ReviewLoopDefinition(channelId, ownerId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);   // posts the card, parks on flow.wait_action
        var messageId = await ReadPostedMessageIdAsync(runId);

        // Two comments while the run is parked — the card stays Open, the run stays Suspended.
        await RespondViaMediatorAsync(ownerId, teamId, messageId, MessageInteractionPolicy.CommentKey, "starting");
        await RespondViaMediatorAsync(ownerId, teamId, messageId, MessageInteractionPolicy.CommentKey, "almost done");

        using (var mid = _fixture.BeginScope())
            (await mid.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "comments don't resolve the wait — the run stays parked");

        // Then the decision — resolves the wait + closes the card.
        await RespondViaMediatorAsync(ownerId, teamId, messageId, "approve", "approved");
        await RunEngineAsync(runId);

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var interaction = MessageInteractionJson.Deserialize((await db.Message.AsNoTracking().SingleAsync(m => m.Id == messageId)).InteractionJson)!;
            interaction.State.ShouldBe(InteractionState.Resolved);
            interaction.Responses.Count.ShouldBe(3, "the full timeline: two comments then the decision");
            interaction.Responses[0].Kind.ShouldBe(InteractionResponseKind.Comment);
            interaction.Responses[1].Kind.ShouldBe(InteractionResponseKind.Comment);
            interaction.Responses[2].Kind.ShouldBe(InteractionResponseKind.Action);
            interaction.Responses[2].Key.ShouldBe("approve", "the resolving decision is logged in the same timeline as the discussion");
        }

        // A comment after the card resolved is rejected — closed to further responses.
        await Should.ThrowAsync<InvalidOperationException>(() => RespondViaMediatorAsync(ownerId, teamId, messageId, MessageInteractionPolicy.CommentKey, "too late"));
    }

    [Fact]
    public async Task Post_form_card_then_submit_injects_the_values_resumes_the_run_and_stamps_them()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, ReviewFormDefinition(channelId, ownerId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);   // posts the form card, parks on flow.wait_action

        Guid messageId;
        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended);
            var post = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "post");
            messageId = Guid.Parse(JsonDocument.Parse(post.OutputsJson).RootElement.GetProperty("messageId").GetString()!);
        }

        // Submitting with an empty required field is rejected server-side (not just a UI hint).
        await Should.ThrowAsync<InvalidOperationException>(() =>
            RespondViaMediatorAsync(ownerId, teamId, messageId, "submit", values: new Dictionary<string, JsonElement> { ["environment"] = JsonSerializer.SerializeToElement("  ") }));

        // The reviewer submits the form — the values are injected into the parked run.
        await RespondViaMediatorAsync(ownerId, teamId, messageId, "submit", values: new Dictionary<string, JsonElement> { ["environment"] = JsonSerializer.SerializeToElement("production") });

        await RunEngineAsync(runId);   // the wait resumes with the submitted values

        using (var verify = _fixture.BeginScope())
        {
            var db = verify.Resolve<CodeSpaceDbContext>();
            (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Success);

            var wait = await db.WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "wait");
            var waitOut = JsonDocument.Parse(wait.OutputsJson).RootElement;
            waitOut.GetProperty("values").GetProperty("environment").GetString()
                .ShouldBe("production", "the submitted form values are injected as the wait node's outputs.values for downstream nodes");

            var message = await db.Message.AsNoTracking().SingleAsync(m => m.Id == messageId);
            var interaction = MessageInteractionJson.Deserialize(message.InteractionJson);
            interaction!.State.ShouldBe(InteractionState.Resolved);
            interaction.Resolution!.Values!["environment"].GetString().ShouldBe("production", "the resolved card mirrors the submitted values");
        }
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedChannelAsync(Guid teamId, Guid ownerId)
    {
        using var scope = _fixture.BeginScope();
        var slug = "respond-" + Guid.NewGuid().ToString("N")[..8];
        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, default);
    }

    private async Task RespondViaMediatorAsync(Guid userId, Guid teamId, Guid messageId, string responseKey, string? comment = null, IReadOnlyDictionary<string, JsonElement>? values = null)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        await scope.Resolve<IMediator>().Send(new RespondToMessageCommand { MessageId = messageId, ResponseKey = responseKey, Comment = comment, Values = values });
    }

    private async Task RespondDirectShouldThrow<TException>(Guid teamId, Guid messageId, string responseKey, Guid actorUserId) where TException : Exception
    {
        await Should.ThrowAsync<TException>(async () =>
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IMessageInteractionService>().RespondAsync(teamId, messageId, responseKey, actorUserId, null, null, default);
        });
    }

    private async Task RespondDirectCommentShouldThrow(Guid teamId, Guid messageId, Guid actorUserId, string comment)
    {
        await Should.ThrowAsync<InvalidOperationException>(async () =>
        {
            using var scope = _fixture.BeginScope();
            await scope.Resolve<IMessageInteractionService>().RespondAsync(teamId, messageId, MessageInteractionPolicy.CommentKey, actorUserId, comment, null, default);
        });
    }

    private async Task<Guid> ReadPostedMessageIdAsync(Guid runId)
    {
        using var verify = _fixture.BeginScope();
        var post = await verify.Resolve<CodeSpaceDbContext>().WorkflowRunNode.AsNoTracking().SingleAsync(n => n.RunId == runId && n.NodeId == "post");
        return Guid.Parse(JsonDocument.Parse(post.OutputsJson).RootElement.GetProperty("messageId").GetString()!);
    }

    private async Task<Guid> CreateWorkflowAsync(Guid teamId, Guid userId, WorkflowDefinition def)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(new CreateWorkflowCommand
        {
            Name = "reviewloop-" + Guid.NewGuid().ToString("N")[..6],
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

    private static WorkflowDefinition ReviewLoopDefinition(Guid channelId, Guid reviewerId) => new()
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
                // Build the inputs as a real object so the channel id / reviewer id interpolate cleanly.
                Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new
                {
                    conversationId = channelId.ToString(),
                    body = "Review PR #9?",
                    actions = new[] { new { key = "approve", label = "Approve" }, new { key = "reject", label = "Reject" } },
                    allowedResponderUserIds = new[] { reviewerId.ToString() },
                })),
            },
            // The wait node parks on the SAME token the card carries — wired from post's `token` output.
            new() { Id = "wait", TypeKey = "flow.wait_action", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "token": "{{nodes.post.outputs.token}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "post" },
            new() { From = "post", To = "wait" },
            new() { From = "wait", To = "end" },
        },
    };

    // Same shape as ReviewLoopDefinition but the card is a FORM (input fields) instead of buttons:
    // the responder submits values mid-run, injected as the wait node's outputs.values.
    private static WorkflowDefinition ReviewFormDefinition(Guid channelId, Guid reviewerId) => new()
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
                Inputs = WorkflowsTestSeed.Json(JsonSerializer.Serialize(new
                {
                    conversationId = channelId.ToString(),
                    body = "Which environment should I deploy to?",
                    form = new
                    {
                        fields = new { type = "object", properties = new { environment = new { type = "string" } }, required = new[] { "environment" } },
                        submitLabel = "Deploy",
                    },
                    allowedResponderUserIds = new[] { reviewerId.ToString() },
                })),
            },
            new() { Id = "wait", TypeKey = "flow.wait_action", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.Json("""{ "token": "{{nodes.post.outputs.token}}" }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition>
        {
            new() { From = "start", To = "post" },
            new() { From = "post", To = "wait" },
            new() { From = "wait", To = "end" },
        },
    };
}

using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
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
    public async Task Under_a_quorum_a_single_approval_records_the_vote_and_the_card_stays_open()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);
        var (runId, token) = await SeedParkedRunAsync(teamId, ownerId);

        var cardId = await PostQuorumCardAsync(channelId, token, count: 2);

        await RespondDirectAsync(teamId, cardId, "approve", ownerId);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var interaction = MessageInteractionJson.Deserialize((await db.Message.AsNoTracking().SingleAsync(m => m.Id == cardId)).InteractionJson)!;

        interaction.State.ShouldBe(InteractionState.Open, "1 of 2 approvals — the card stays open");
        interaction.Responses.Count(r => r.Kind == InteractionResponseKind.Action).ShouldBe(1, "the vote is recorded in the log");
        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status.ShouldBe(WorkflowWaitStatuses.Pending, "the wait is not resolved");
        (await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status.ShouldBe(WorkflowRunStatus.Suspended, "the run stays parked until quorum");
    }

    [Fact]
    public async Task A_veto_resolves_the_wait_immediately_even_under_a_quorum()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);
        var (runId, token) = await SeedParkedRunAsync(teamId, ownerId);

        var cardId = await PostQuorumCardAsync(channelId, token, count: 2);

        await RespondDirectAsync(teamId, cardId, "request_changes", ownerId, "needs work");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var interaction = MessageInteractionJson.Deserialize((await db.Message.AsNoTracking().SingleAsync(m => m.Id == cardId)).InteractionJson)!;

        interaction.State.ShouldBe(InteractionState.Resolved, "a veto short-circuits the 2-quorum");
        interaction.Resolution!.ResponseKey.ShouldBe("request_changes");
        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status.ShouldBe(WorkflowWaitStatuses.Resolved);
    }

    [Fact]
    public async Task A_two_person_quorum_resolves_only_on_the_second_distinct_approval()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);
        var (runId, token) = await SeedParkedRunAsync(teamId, ownerId);

        var cardId = await PostQuorumCardAsync(channelId, token, count: 2);

        var member2 = await SeedUserAsync();
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IConversationService>().AddMemberAsync(teamId, ownerId, channelId, member2, default);

        await RespondDirectAsync(teamId, cardId, "approve", ownerId);   // 1st distinct approval

        using (var mid = _fixture.BeginScope())
            (await mid.Resolve<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status
                .ShouldBe(WorkflowWaitStatuses.Pending, "one approval is short of the 2-quorum");

        await RespondDirectAsync(teamId, cardId, "approve", member2);   // 2nd distinct approval → reaches quorum

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var interaction = MessageInteractionJson.Deserialize((await db.Message.AsNoTracking().SingleAsync(m => m.Id == cardId)).InteractionJson)!;

        interaction.State.ShouldBe(InteractionState.Resolved, "two distinct approvals reach the quorum");
        interaction.Responses.Count(r => r.Kind == InteractionResponseKind.Action).ShouldBe(2);
        (await db.WorkflowRunWait.AsNoTracking().SingleAsync(w => w.RunId == runId)).Status.ShouldBe(WorkflowWaitStatuses.Resolved);
    }

    [Fact]
    public async Task A_workflow_authored_quorum_card_resolves_only_after_two_approvals_then_resumes()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var channelId = await SeedChannelAsync(teamId, ownerId);

        var member2 = await SeedUserAsync();
        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IConversationService>().AddMemberAsync(teamId, ownerId, channelId, member2, default);

        var workflowId = await CreateWorkflowAsync(teamId, ownerId, QuorumReviewDefinition(channelId));
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);

        await RunEngineAsync(runId);   // post_message posts the card AND parks itself (waitForResponse + quorum authored on the node)
        var messageId = await ReadCardMessageIdAsync(channelId);   // the self-waiting node suspends → no messageId output; read the posted card by channel

        await RespondDirectAsync(teamId, messageId, "approve", ownerId);
        using (var mid = _fixture.BeginScope())
            (await mid.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
                .ShouldBe(WorkflowRunStatus.Suspended, "one approval is short of the node-authored 2-quorum — the run stays parked");

        await RespondDirectAsync(teamId, messageId, "approve", member2);   // second distinct approval → reaches quorum
        await RunEngineAsync(runId);

        using var verify = _fixture.BeginScope();
        (await verify.Resolve<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == runId)).Status
            .ShouldBe(WorkflowRunStatus.Success, "the second distinct approval reaches the quorum, resolves the node's own wait, and the run resumes end-to-end");
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

    private async Task<Guid> ReadCardMessageIdAsync(Guid channelId)
    {
        using var verify = _fixture.BeginScope();
        return (await verify.Resolve<CodeSpaceDbContext>().Message.AsNoTracking()
            .SingleAsync(m => m.ConversationId == channelId && m.InteractionJson != null && m.DeletedDate == null)).Id;
    }

    private async Task RespondDirectAsync(Guid teamId, Guid messageId, string responseKey, Guid actorUserId, string? comment = null)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMessageInteractionService>().RespondAsync(teamId, messageId, responseKey, actorUserId, comment, null, default);
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"resp-{userId:N}@test.local", Name = $"resp-{userId:N}" });
        await db.SaveChangesAsync();
        return userId;
    }

    /// <summary>Seed a manual run, park it on an Action wait keyed by a fresh token, and return both (mirrors what a parked flow.wait_action writes) — so a respond can resolve a real wait without driving the engine.</summary>
    private async Task<(Guid RunId, string Token)> SeedParkedRunAsync(Guid teamId, Guid ownerId)
    {
        var workflowId = await CreateWorkflowAsync(teamId, ownerId, WorkflowsTestSeed.MinimalDefinition());
        var runId = await WorkflowsTestSeed.SeedManualRunAsync(_fixture, workflowId, teamId);
        var token = "tok-" + Guid.NewGuid().ToString("N")[..8];

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        (await db.WorkflowRun.SingleAsync(r => r.Id == runId)).Status = WorkflowRunStatus.Suspended;
        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = Guid.NewGuid(), RunId = runId, NodeId = "wait", IterationKey = string.Empty,
            WaitKind = WorkflowWaitKinds.Action, Token = token, Status = WorkflowWaitStatuses.Pending, CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        return (runId, token);
    }

    private async Task<Guid> PostQuorumCardAsync(Guid channelId, string token, int count)
    {
        var card = new MessageInteraction
        {
            Component = new ActionButtonsComponent
            {
                Buttons = new List<InteractionButton>
                {
                    new() { Key = "approve", Label = "Approve" },
                    new() { Key = "request_changes", Label = "Request changes", Vetoes = true },
                },
            },
            Target = new WorkflowWaitTarget { Token = token },
            Resolve = new ResolvePolicy { Kind = ResolvePolicyKind.Quorum, Count = count },
        };

        using var scope = _fixture.BeginScope();
        return (await scope.Resolve<IChatBotService>().PostAsBotAsync(channelId, "Review PR?", card, default)).Id;
    }

    // A post_message that AUTHORS a 2-approval quorum on the node itself (config) + waits inline — the realistic
    // "team review" the operator builds in the editor. allowedResponderUserIds omitted = any conversation member.
    private static WorkflowDefinition QuorumReviewDefinition(Guid channelId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
            new()
            {
                Id = "post",
                TypeKey = "chat.post_message",
                Config = WorkflowsTestSeed.Json("""{ "waitForResponse": true, "resolve": { "mode": "quorum", "count": 2 } }"""),
                Inputs = WorkflowsTestSeed.Json($$"""
                    { "conversationId": "{{channelId}}", "body": "Review PR #9?",
                      "actions": [ {"key":"approve","label":"Approve"}, {"key":"request_changes","label":"Request changes","vetoes":true} ] }
                    """),
            },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = WorkflowsTestSeed.EmptyJson(), Inputs = WorkflowsTestSeed.EmptyJson() },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "post" }, new() { From = "post", To = "end" } },
    };

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

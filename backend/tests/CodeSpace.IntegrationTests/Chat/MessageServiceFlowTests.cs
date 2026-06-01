using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Chat;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// Contract for <see cref="IMessageService"/> against real Postgres — posting, keyset
/// pagination, edit / soft-delete, the forward-only read cursor, and the generic
/// <c>@</c>-reference extraction + reverse index. Membership and tenancy are gated on every
/// path. The pure token grammar is unit-tested separately (MessageReferenceParserTests); here we
/// prove the grammar's output actually lands in (and is reverse-lookupable from) the database.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MessageServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public MessageServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── Post + reference extraction ────────────────────────────────────────────────

    [Fact]
    public async Task Post_persists_message_and_extracts_distinct_references()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var mentioned = Guid.NewGuid();

        var view = await PostAsync(teamId, ownerId, channelId,
            $"hey <user:{mentioned}|Alice> see <pull_request:repo1#42|PR 42> and again <user:{mentioned}|Alice>");

        view.AuthorUserId.ShouldBe(ownerId);
        view.IsDeleted.ShouldBeFalse();
        view.References.Count.ShouldBe(2, customMessage: "Duplicate (type,id) must collapse to one reference row.");
        view.References.ShouldContain(r => r.RefType == "user" && r.RefId == mentioned.ToString() && r.Label == "Alice");
        view.References.ShouldContain(r => r.RefType == "pull_request" && r.RefId == "repo1#42" && r.Label == "PR 42");

        using var verify = _fixture.BeginScope();
        var rows = await verify.Resolve<CodeSpaceDbContext>().MessageReference.AsNoTracking()
            .Where(r => r.MessageId == view.Id).ToListAsync();
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(r => r.TeamId == teamId);
    }

    [Fact]
    public async Task Post_returns_body_verbatim_keeping_inline_tokens()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        const string body = "look at <code_location:repo1:abc:src/Foo.cs:42|Foo.cs:42> please";

        var view = await PostAsync(teamId, ownerId, channelId, body);

        view.Body.ShouldBe(body, customMessage: "Body is stored verbatim so it renders standalone; tokens are NOT stripped.");
    }

    [Fact]
    public async Task Listed_reference_surfaces_the_label_round_tripped_through_storage()
    {
        // The post response carries the parser's in-memory label; listing re-reads it from the
        // jsonb cache. This pins the serialize → store → deserialize path end-to-end.
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        await PostAsync(teamId, ownerId, channelId, "ping <user:u-42|Alice Cooper>");

        var page = await ListAsync(teamId, ownerId, channelId, beforeId: null, limit: 50);

        var reference = page.Messages.ShouldHaveSingleItem().References.ShouldHaveSingleItem();
        reference.RefId.ShouldBe("u-42");
        reference.Label.ShouldBe("Alice Cooper", customMessage: "The cached label must survive the jsonb round trip on read.");
    }

    [Fact]
    public async Task Post_by_non_member_throws()
    {
        var (teamId, _, channelId) = await SeedChannelWithOwnerAsync();
        var outsider = await SeedUserAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => PostAsync(teamId, outsider, channelId, "I shouldn't be here"));
    }

    [Fact]
    public async Task Post_under_another_team_scope_throws_even_for_a_real_member()
    {
        // The channel + the owner's membership live in team A. Posting with team B's id must
        // fail the (team_id) membership gate — tenancy isolation, not just "is a member".
        var (teamA, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var (teamB, _, _) = await SeedChannelWithOwnerAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => PostAsync(teamB, ownerId, channelId, "cross-tenant"));
    }

    [Fact]
    public async Task Post_with_empty_body_throws()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();

        await Should.ThrowAsync<ArgumentException>(() => PostAsync(teamId, ownerId, channelId, "   "));
    }

    [Fact]
    public async Task Post_at_the_body_length_limit_succeeds_but_one_over_throws()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();

        var atLimit = await PostAsync(teamId, ownerId, channelId, new string('x', MessageService.MaxBodyLength));
        atLimit.Body.Length.ShouldBe(MessageService.MaxBodyLength, customMessage: "A body exactly at the cap must post.");

        await Should.ThrowAsync<ArgumentException>(
            () => PostAsync(teamId, ownerId, channelId, new string('x', MessageService.MaxBodyLength + 1)));
    }

    [Fact]
    public async Task Post_reply_to_message_in_same_conversation_succeeds_but_foreign_parent_throws()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var otherChannel = await SeedChannelAsync(teamId, ownerId, "other");

        var parent = await PostAsync(teamId, ownerId, channelId, "parent");

        var reply = await PostAsync(teamId, ownerId, channelId, "reply", parent.Id);
        reply.ReplyToMessageId.ShouldBe(parent.Id);

        // A reply pointing at a message in a different conversation is rejected.
        var foreignParent = await PostAsync(teamId, ownerId, otherChannel, "elsewhere");
        await Should.ThrowAsync<InvalidOperationException>(() => PostAsync(teamId, ownerId, channelId, "bad reply", foreignParent.Id));
    }

    // ─── List + keyset pagination (newest-first) ────────────────────────────────────

    [Fact]
    public async Task List_returns_messages_newest_first()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var ids = await SeedSequentialMessagesAsync(teamId, ownerId, channelId, 3);

        var page = await ListAsync(teamId, ownerId, channelId, beforeId: null, limit: 50);

        page.Messages.Select(m => m.Id).ShouldBe(new[] { ids[2], ids[1], ids[0] },
            customMessage: "Newest message must come first (DESC over the time-sortable id).");
        page.HasMore.ShouldBeFalse();
        page.NextBeforeId.ShouldBeNull();
    }

    [Fact]
    public async Task List_pages_through_history_with_the_cursor_without_skips_or_dupes()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var ids = await SeedSequentialMessagesAsync(teamId, ownerId, channelId, 5);   // ids[0] oldest … ids[4] newest

        var page1 = await ListAsync(teamId, ownerId, channelId, beforeId: null, limit: 2);
        page1.Messages.Select(m => m.Id).ShouldBe(new[] { ids[4], ids[3] });
        page1.HasMore.ShouldBeTrue();
        page1.NextBeforeId.ShouldBe(ids[3]);

        var page2 = await ListAsync(teamId, ownerId, channelId, page1.NextBeforeId, limit: 2);
        page2.Messages.Select(m => m.Id).ShouldBe(new[] { ids[2], ids[1] });
        page2.HasMore.ShouldBeTrue();
        page2.NextBeforeId.ShouldBe(ids[1]);

        var page3 = await ListAsync(teamId, ownerId, channelId, page2.NextBeforeId, limit: 2);
        page3.Messages.Select(m => m.Id).ShouldBe(new[] { ids[0] });
        page3.HasMore.ShouldBeFalse(customMessage: "The final page must report no more history.");
        page3.NextBeforeId.ShouldBeNull();
    }

    [Fact]
    public async Task List_clamps_an_oversized_limit_and_stays_scoped_to_the_conversation()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var otherChannel = await SeedChannelAsync(teamId, ownerId, "other");

        await PostAsync(teamId, ownerId, channelId, "in scope");
        await PostAsync(teamId, ownerId, otherChannel, "out of scope");

        var page = await ListAsync(teamId, ownerId, channelId, beforeId: null, limit: 9999);

        page.Messages.Count.ShouldBe(1, customMessage: "List must only return the requested conversation's messages.");
        page.Messages[0].Body.ShouldBe("in scope");
    }

    [Fact]
    public async Task List_with_a_non_positive_limit_is_clamped_to_one()
    {
        // A 0 / negative limit must not blow up or return everything — it clamps to 1.
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        await SeedSequentialMessagesAsync(teamId, ownerId, channelId, 3);

        var page = await ListAsync(teamId, ownerId, channelId, beforeId: null, limit: 0);

        page.Messages.Count.ShouldBe(1, customMessage: "limit<=0 must clamp to 1, not return the whole conversation.");
        page.HasMore.ShouldBeTrue(customMessage: "With 3 messages and an effective page of 1, there must be more.");
    }

    [Fact]
    public async Task List_by_non_member_throws()
    {
        var (teamId, _, channelId) = await SeedChannelWithOwnerAsync();
        var outsider = await SeedUserAsync();

        await Should.ThrowAsync<InvalidOperationException>(() => ListAsync(teamId, outsider, channelId, null, 50));
    }

    [Fact]
    public async Task List_renders_a_deleted_message_as_a_blanked_tombstone()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();

        var msg = await PostAsync(teamId, ownerId, channelId, "secret content <user:x|x>");
        await DeleteAsync(teamId, ownerId, msg.Id);

        var page = await ListAsync(teamId, ownerId, channelId, null, 50);

        var tomb = page.Messages.ShouldHaveSingleItem();
        tomb.Id.ShouldBe(msg.Id);
        tomb.IsDeleted.ShouldBeTrue();
        tomb.Body.ShouldBeEmpty(customMessage: "Deleted content must never leave the server.");
        tomb.References.ShouldBeEmpty();
    }

    // ─── Edit ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Edit_by_author_updates_body_marks_edited_and_reextracts_references()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();

        var msg = await PostAsync(teamId, ownerId, channelId, "mentions <pull_request:r#1|PR 1>");

        MessageView edited;
        using (var scope = _fixture.BeginScope())
            edited = await scope.Resolve<IMessageService>().EditAsync(teamId, ownerId, msg.Id, "now mentions <workflow:wf9|Deploy>", default);

        edited.Body.ShouldBe("now mentions <workflow:wf9|Deploy>");
        edited.EditedDate.ShouldNotBeNull();
        edited.References.ShouldHaveSingleItem().RefType.ShouldBe("workflow");

        using var verify = _fixture.BeginScope();
        var rows = await verify.Resolve<CodeSpaceDbContext>().MessageReference.AsNoTracking().Where(r => r.MessageId == msg.Id).ToListAsync();
        rows.ShouldHaveSingleItem().RefId.ShouldBe("wf9", customMessage: "Editing must drop the old reference and persist the new one.");
    }

    [Fact]
    public async Task Edit_by_non_author_throws()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var other = await SeedUserAsync();
        await JoinAsync(teamId, ownerId, channelId, other);

        var msg = await PostAsync(teamId, ownerId, channelId, "owner's message");

        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<InvalidOperationException>(
            () => scope.Resolve<IMessageService>().EditAsync(teamId, other, msg.Id, "hijacked", default));
    }

    [Fact]
    public async Task Edit_to_a_body_over_the_limit_throws()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var msg = await PostAsync(teamId, ownerId, channelId, "short");

        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<ArgumentException>(
            () => scope.Resolve<IMessageService>().EditAsync(teamId, ownerId, msg.Id, new string('x', MessageService.MaxBodyLength + 1), default));
    }

    // ─── Delete (soft) ───────────────────────────────────────────────────────────--

    [Fact]
    public async Task Delete_by_author_soft_deletes_and_drops_references()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();

        var msg = await PostAsync(teamId, ownerId, channelId, "bye <user:u1|U1>");
        await DeleteAsync(teamId, ownerId, msg.Id);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var row = await db.Message.AsNoTracking().SingleAsync(m => m.Id == msg.Id);
        row.DeletedDate.ShouldNotBeNull();

        var refCount = await db.MessageReference.AsNoTracking().CountAsync(r => r.MessageId == msg.Id);
        refCount.ShouldBe(0, customMessage: "A deleted message must stop appearing as a backlink — its references are dropped.");
    }

    [Fact]
    public async Task Delete_by_non_author_throws()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var other = await SeedUserAsync();
        await JoinAsync(teamId, ownerId, channelId, other);

        var msg = await PostAsync(teamId, ownerId, channelId, "owner's message");

        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<InvalidOperationException>(
            () => scope.Resolve<IMessageService>().DeleteAsync(teamId, other, msg.Id, default));
    }

    // ─── Read cursor (forward-only) ─────────────────────────────────────────────────

    [Fact]
    public async Task MarkRead_advances_the_member_cursor()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var ids = await SeedSequentialMessagesAsync(teamId, ownerId, channelId, 3);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IMessageService>().MarkReadAsync(teamId, ownerId, channelId, ids[2], default);

        (await ReadCursorAsync(channelId, ownerId)).ShouldBe(ids[2]);
    }

    [Fact]
    public async Task MarkRead_is_forward_only_and_never_regresses()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var ids = await SeedSequentialMessagesAsync(teamId, ownerId, channelId, 3);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IMessageService>().MarkReadAsync(teamId, ownerId, channelId, ids[2], default);

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IMessageService>().MarkReadAsync(teamId, ownerId, channelId, ids[0], default);   // older → must no-op

        (await ReadCursorAsync(channelId, ownerId)).ShouldBe(ids[2],
            customMessage: "A stale client marking an OLDER message read must not drag the cursor backward.");
    }

    [Fact]
    public async Task MarkRead_by_non_member_throws()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var outsider = await SeedUserAsync();
        var msg = await PostAsync(teamId, ownerId, channelId, "hi");

        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<InvalidOperationException>(
            () => scope.Resolve<IMessageService>().MarkReadAsync(teamId, outsider, channelId, msg.Id, default));
    }

    [Fact]
    public async Task MarkRead_with_a_message_from_another_conversation_throws()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var otherChannel = await SeedChannelAsync(teamId, ownerId, "other");
        var foreign = await PostAsync(teamId, ownerId, otherChannel, "elsewhere");

        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<InvalidOperationException>(
            () => scope.Resolve<IMessageService>().MarkReadAsync(teamId, ownerId, channelId, foreign.Id, default));
    }

    // ─── Reverse index (the @ feature foundation) ───────────────────────────────────

    [Fact]
    public async Task References_to_the_same_target_are_reverse_lookupable_by_team_type_id()
    {
        // Two messages mention the same PR. "Every message that references PR repo1#42" — the
        // backlink / @-inbox query — is a single indexed read on (team_id, ref_type, ref_id).
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();

        var m1 = await PostAsync(teamId, ownerId, channelId, "fixing <pull_request:repo1#42|PR 42>");
        var m2 = await PostAsync(teamId, ownerId, channelId, "still on <pull_request:repo1#42|PR 42>");

        using var verify = _fixture.BeginScope();
        var mentioningMessageIds = await verify.Resolve<CodeSpaceDbContext>().MessageReference.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.RefType == "pull_request" && r.RefId == "repo1#42")
            .Select(r => r.MessageId)
            .ToListAsync();

        mentioningMessageIds.Count.ShouldBe(2);
        mentioningMessageIds.ShouldContain(m1.Id);
        mentioningMessageIds.ShouldContain(m2.Id);
    }

    // ─── Interactive messages (action cards) ────────────────────────────────────────

    [Fact]
    public async Task Post_interactive_persists_the_component_and_keeps_the_token_server_side()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();

        var view = await PostInteractiveAsync(teamId, ownerId, channelId, "Review PR #42?", SampleInteraction("tok-secret-42"));

        var component = view.Interaction.ShouldNotBeNull().Component.ShouldBeOfType<ActionButtonsComponent>();
        component.Buttons.Select(b => b.Key).ShouldBe(new[] { "approve", "reject" });
        view.Interaction!.State.ShouldBe(InteractionState.Open);

        // The view type has NO target field (compile-time guarantee). Prove the token IS persisted
        // server-side (so the respond endpoint can re-derive it) — it lives in the stored jsonb only.
        using var verify = _fixture.BeginScope();
        var stored = await verify.Resolve<CodeSpaceDbContext>().Message.AsNoTracking().SingleAsync(m => m.Id == view.Id);
        stored.InteractionJson.ShouldNotBeNull();
        stored.InteractionJson!.ShouldContain("tok-secret-42", customMessage: "the wait token is persisted server-side for the respond endpoint to re-derive");
        stored.InteractionJson.ShouldContain("workflow_wait");
    }

    [Fact]
    public async Task Listed_interactive_message_round_trips_the_component_through_the_keyset_query()
    {
        // The keyset page query selects an explicit column list; this pins that interaction_json is
        // in it and survives the jsonb round trip on read (not just on the post response).
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        await PostInteractiveAsync(teamId, ownerId, channelId, "Approve?", SampleInteraction("tok-list"));

        var page = await ListAsync(teamId, ownerId, channelId, beforeId: null, limit: 50);

        var interaction = page.Messages.ShouldHaveSingleItem().Interaction.ShouldNotBeNull();
        var component = interaction.Component.ShouldBeOfType<ActionButtonsComponent>();
        component.Buttons.Count.ShouldBe(2, customMessage: "the polymorphic component must survive the FromSqlRaw keyset read + jsonb deserialize");

        // Token-leak guard: the read path drops the Target, so the JSON actually serialized to the
        // client can never carry the server-side wait token. If someone re-adds Target to the view
        // type, this fails — the token is the credential the respond endpoint re-derives, not a client value.
        JsonSerializer.Serialize(interaction).ShouldNotContain("tok-list", Case.Insensitive,
            customMessage: "the wait token must stay server-side — the client-facing interaction view must omit it");
    }

    [Fact]
    public async Task Plain_message_has_a_null_interaction()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();

        var view = await PostAsync(teamId, ownerId, channelId, "just text");

        view.Interaction.ShouldBeNull(customMessage: "a plain message must read back with no interaction — the new column is purely additive");
    }

    [Fact]
    public async Task Deleted_interactive_message_blanks_the_interaction()
    {
        var (teamId, ownerId, channelId) = await SeedChannelWithOwnerAsync();
        var msg = await PostInteractiveAsync(teamId, ownerId, channelId, "Approve?", SampleInteraction("tok-del"));

        await DeleteAsync(teamId, ownerId, msg.Id);

        var page = await ListAsync(teamId, ownerId, channelId, null, 50);
        page.Messages.ShouldHaveSingleItem().Interaction.ShouldBeNull(customMessage: "a tombstone exposes no interaction, mirroring the blanked body / references");
    }

    private static MessageInteraction SampleInteraction(string token) => new()
    {
        Component = new ActionButtonsComponent
        {
            Buttons = new List<InteractionButton>
            {
                new() { Key = "approve", Label = "Approve", Style = InteractionButtonStyle.Primary },
                new() { Key = "reject", Label = "Reject", Style = InteractionButtonStyle.Danger },
            },
        },
        Target = new WorkflowWaitTarget { Token = token },
    };

    // ─── Helpers ───────────────────────────────────────────────────────────────────

    private async Task<MessageView> PostInteractiveAsync(Guid teamId, Guid authorId, Guid conversationId, string body, MessageInteraction interaction)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IMessageService>().PostInteractiveAsync(teamId, authorId, conversationId, body, interaction, default);
    }

    private async Task<MessageView> PostAsync(Guid teamId, Guid authorId, Guid conversationId, string body, Guid? replyTo = null)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IMessageService>().PostAsync(teamId, authorId, conversationId, body, replyTo, default);
    }

    private async Task<MessagePage> ListAsync(Guid teamId, Guid userId, Guid conversationId, Guid? beforeId, int limit)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IMessageService>().ListAsync(teamId, userId, conversationId, beforeId, limit, default);
    }

    private async Task DeleteAsync(Guid teamId, Guid actorId, Guid messageId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMessageService>().DeleteAsync(teamId, actorId, messageId, default);
    }

    private async Task JoinAsync(Guid teamId, Guid actorId, Guid channelId, Guid newMemberId)
    {
        using var scope = _fixture.BeginScope();
        await scope.Resolve<IConversationService>().AddMemberAsync(teamId, actorId, channelId, newMemberId, default);
    }

    /// <summary>Posts <paramref name="count"/> messages oldest-first, spacing them so each gets a
    /// distinct UUID-v7 millisecond timestamp — that makes id order == post order deterministic
    /// for the ordering / pagination assertions. Returns ids in post (chronological) order.</summary>
    private async Task<List<Guid>> SeedSequentialMessagesAsync(Guid teamId, Guid authorId, Guid channelId, int count)
    {
        var ids = new List<Guid>(count);
        for (var i = 1; i <= count; i++)
        {
            var msg = await PostAsync(teamId, authorId, channelId, $"message {i}");
            ids.Add(msg.Id);
            await Task.Delay(3);
        }
        return ids;
    }

    private async Task<Guid?> ReadCursorAsync(Guid channelId, Guid userId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<CodeSpaceDbContext>().ConversationMember.AsNoTracking()
            .Where(m => m.ConversationId == channelId && m.UserId == userId)
            .Select(m => m.LastReadMessageId)
            .SingleAsync();
    }

    private async Task<(Guid TeamId, Guid OwnerId, Guid ChannelId)> SeedChannelWithOwnerAsync()
    {
        var (teamId, ownerId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScope();
        var channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, "General", "general", false, ownerId, default);
        return (teamId, ownerId, channelId);
    }

    /// <summary>Another channel in the SAME team, owned by the same user — for cross-conversation
    /// tests (foreign reply parent, foreign read cursor, list scoping) where the actor must be a
    /// real member of both conversations.</summary>
    private async Task<Guid> SeedChannelAsync(Guid teamId, Guid ownerId, string slug)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, false, ownerId, default);
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"msg-{userId:N}@test.local", Name = $"msg-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"msg-{teamId:N}", Name = "Msg Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"msg-{userId:N}@test.local", Name = $"msg-{userId:N}" });
        await db.SaveChangesAsync();
        return userId;
    }
}

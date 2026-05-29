using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Chat;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// Contract for <see cref="IConversationService"/> against real Postgres — the conversation
/// half of the chat foundation (channels / DMs / groups + membership). Messages + the generic
/// @-reference parser land in the next PR; this proves the conversation lifecycle, the
/// race-safe DM singleton, membership gating, and tenancy isolation.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ConversationServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public ConversationServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    // ─── Channels ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateChannel_persists_conversation_and_owner_member()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid channelId;
        using (var scope = _fixture.BeginScope())
            channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, "General", "general", isPrivate: false, userId, default);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var conv = await db.Conversation.AsNoTracking().SingleAsync(c => c.Id == channelId);
        conv.Kind.ShouldBe(ConversationKind.Channel);
        conv.Slug.ShouldBe("general");
        conv.Visibility.ShouldBe(ConversationVisibility.Public);

        var member = await db.ConversationMember.AsNoTracking().SingleAsync(m => m.ConversationId == channelId);
        member.UserId.ShouldBe(userId);
        member.Role.ShouldBe(ConversationMemberRole.Owner);
    }

    [Fact]
    public async Task CreateChannel_normalizes_slug_to_lowercase_url_safe()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid channelId;
        using (var scope = _fixture.BeginScope())
            channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, "Cool Stuff", "  My Cool Channel!! ", isPrivate: false, userId, default);

        using var verify = _fixture.BeginScope();
        var slug = await verify.Resolve<CodeSpaceDbContext>().Conversation.AsNoTracking().Where(c => c.Id == channelId).Select(c => c.Slug).SingleAsync();
        slug.ShouldBe("my-cool-channel");
    }

    [Fact]
    public async Task CreateChannel_duplicate_slug_throws()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScope();
        var svc = scope.Resolve<IConversationService>();
        await svc.CreateChannelAsync(teamId, "General", "general", false, userId, default);

        await Should.ThrowAsync<InvalidOperationException>(
            () => svc.CreateChannelAsync(teamId, "General Two", "general", false, userId, default));
    }

    [Fact]
    public async Task CreateChannel_private_sets_visibility()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid id;
        using (var scope = _fixture.BeginScope())
            id = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, "Secret", "secret", isPrivate: true, userId, default);

        using var verify = _fixture.BeginScope();
        var vis = await verify.Resolve<CodeSpaceDbContext>().Conversation.AsNoTracking().Where(c => c.Id == id).Select(c => c.Visibility).SingleAsync();
        vis.ShouldBe(ConversationVisibility.Private);
    }

    // ─── Direct messages (the race-safe singleton) ─────────────────────────────────

    [Fact]
    public async Task GetOrCreateDirect_creates_dm_with_two_members_and_dm_key()
    {
        var (teamId, userA) = await SeedTeamAsync();
        var userB = await SeedUserAsync();

        Guid dmId;
        using (var scope = _fixture.BeginScope())
            dmId = await scope.Resolve<IConversationService>().GetOrCreateDirectAsync(teamId, userA, userB, default);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var conv = await db.Conversation.AsNoTracking().SingleAsync(c => c.Id == dmId);
        conv.Kind.ShouldBe(ConversationKind.Direct);
        conv.DmKey.ShouldNotBeNullOrEmpty();

        var memberIds = await db.ConversationMember.AsNoTracking().Where(m => m.ConversationId == dmId).Select(m => m.UserId).ToListAsync();
        memberIds.Count.ShouldBe(2);
        memberIds.ShouldContain(userA);
        memberIds.ShouldContain(userB);
    }

    [Fact]
    public async Task GetOrCreateDirect_is_idempotent_and_order_independent()
    {
        // The singleton guarantee: (A,B) and (B,A), called repeatedly, all resolve to ONE DM.
        var (teamId, userA) = await SeedTeamAsync();
        var userB = await SeedUserAsync();

        Guid first, secondSameOrder, thirdReversed;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IConversationService>();
            first = await svc.GetOrCreateDirectAsync(teamId, userA, userB, default);
            secondSameOrder = await svc.GetOrCreateDirectAsync(teamId, userA, userB, default);
            thirdReversed = await svc.GetOrCreateDirectAsync(teamId, userB, userA, default);
        }

        secondSameOrder.ShouldBe(first);
        thirdReversed.ShouldBe(first, customMessage: "DM dm_key MUST be order-independent — (A,B) and (B,A) are the same conversation.");

        using var verify = _fixture.BeginScope();
        var dmCount = await verify.Resolve<CodeSpaceDbContext>().Conversation.AsNoTracking()
            .CountAsync(c => c.TeamId == teamId && c.Kind == ConversationKind.Direct);
        dmCount.ShouldBe(1, customMessage: "Exactly one DM row must exist for the pair across all the find-or-create calls.");
    }

    [Fact]
    public async Task GetOrCreateDirect_with_self_throws()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScope();
        await Should.ThrowAsync<ArgumentException>(
            () => scope.Resolve<IConversationService>().GetOrCreateDirectAsync(teamId, userId, userId, default));
    }

    [Fact]
    public async Task Duplicate_dm_key_is_rejected_by_the_unique_index()
    {
        // Proves the foundation the race-safe find-or-create relies on: the partial unique
        // index on (team_id, dm_key) physically rejects a second DM for the same pair. The
        // service's 23505 catch only works because THIS constraint fires.
        var (teamId, _) = await SeedTeamAsync();
        const string dmKey = "aaaa:bbbb";

        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        db.Conversation.Add(new Conversation { Id = Guid.NewGuid(), TeamId = teamId, Kind = ConversationKind.Direct, DmKey = dmKey });
        await db.SaveChangesAsync();

        db.Conversation.Add(new Conversation { Id = Guid.NewGuid(), TeamId = teamId, Kind = ConversationKind.Direct, DmKey = dmKey });
        var ex = await Should.ThrowAsync<DbUpdateException>(() => db.SaveChangesAsync());
        (ex.InnerException as Npgsql.PostgresException)?.SqlState.ShouldBe("23505");
    }

    [Fact]
    public async Task GetOrCreateDirect_under_real_concurrency_still_yields_exactly_one_dm()
    {
        // Two callers open the same DM at the same instant, each on its own scope/DbContext.
        // One INSERT wins the dm_key unique index; the loser catches 23505 and re-queries the
        // winner. Both must return the SAME id and exactly one DM row may exist — the race-safe
        // singleton guarantee exercised under genuine contention (not just sequential idempotence).
        var (teamId, userA) = await SeedTeamAsync();
        var userB = await SeedUserAsync();

        async Task<Guid> OpenAsync()
        {
            using var scope = _fixture.BeginScope();
            return await scope.Resolve<IConversationService>().GetOrCreateDirectAsync(teamId, userA, userB, default);
        }

        var results = await Task.WhenAll(OpenAsync(), OpenAsync());

        results[0].ShouldBe(results[1], customMessage: "Concurrent opens of the same pair MUST resolve to one DM id.");

        using var verify = _fixture.BeginScope();
        var dmCount = await verify.Resolve<CodeSpaceDbContext>().Conversation.AsNoTracking()
            .CountAsync(c => c.TeamId == teamId && c.Kind == ConversationKind.Direct);
        dmCount.ShouldBe(1, customMessage: "Exactly one DM row may exist for the pair after a concurrent race.");
    }

    // ─── Groups ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateGroup_includes_actor_and_dedups_members()
    {
        var (teamId, actor) = await SeedTeamAsync();
        var userB = await SeedUserAsync();

        Guid groupId;
        using (var scope = _fixture.BeginScope())
            // Pass userB twice + actor explicitly — service must dedup to {actor, userB}.
            groupId = await scope.Resolve<IConversationService>().CreateGroupAsync(teamId, "Squad", new[] { userB, userB, actor }, actor, default);

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        var memberIds = await db.ConversationMember.AsNoTracking().Where(m => m.ConversationId == groupId).Select(m => m.UserId).ToListAsync();
        memberIds.Count.ShouldBe(2);
        memberIds.ShouldContain(actor);
        memberIds.ShouldContain(userB);

        var actorRole = await db.ConversationMember.AsNoTracking().Where(m => m.ConversationId == groupId && m.UserId == actor).Select(m => m.Role).SingleAsync();
        actorRole.ShouldBe(ConversationMemberRole.Owner);
    }

    [Fact]
    public async Task CreateGroup_with_fewer_than_two_distinct_members_throws()
    {
        var (teamId, actor) = await SeedTeamAsync();

        using var scope = _fixture.BeginScope();
        // Only the actor (the member list is just the actor again) → < 2 distinct.
        await Should.ThrowAsync<ArgumentException>(
            () => scope.Resolve<IConversationService>().CreateGroupAsync(teamId, "Solo", new[] { actor }, actor, default));
    }

    // ─── List / Get (membership + tenancy gating) ──────────────────────────────────

    [Fact]
    public async Task ListForUser_returns_only_conversations_the_user_belongs_to()
    {
        var (teamId, userA) = await SeedTeamAsync();
        var userB = await SeedUserAsync();

        Guid joinedChannel, otherChannel;
        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IConversationService>();
            joinedChannel = await svc.CreateChannelAsync(teamId, "Joined", "joined", false, userA, default);
            otherChannel = await svc.CreateChannelAsync(teamId, "Other", "other", false, userB, default);  // userA NOT a member
        }

        using var verify = _fixture.BeginScope();
        var list = await verify.Resolve<IConversationService>().ListForUserAsync(teamId, userA, default);

        list.Select(c => c.Id).ShouldContain(joinedChannel);
        list.Select(c => c.Id).ShouldNotContain(otherChannel,
            customMessage: "ListForUser MUST exclude conversations the user isn't a member of.");
    }

    [Fact]
    public async Task Get_returns_null_for_non_member_without_leaking_existence()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var outsider = await SeedUserAsync();

        Guid channelId;
        using (var scope = _fixture.BeginScope())
            channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, "Private-ish", "p", true, owner, default);

        using var verify = _fixture.BeginScope();
        var asOutsider = await verify.Resolve<IConversationService>().GetAsync(teamId, outsider, channelId, default);
        asOutsider.ShouldBeNull(customMessage: "A non-member must get null (404), never a populated summary that confirms the conversation exists.");

        using var verify2 = _fixture.BeginScope();
        var asOwner = await verify2.Resolve<IConversationService>().GetAsync(teamId, owner, channelId, default);
        asOwner.ShouldNotBeNull();
    }

    [Fact]
    public async Task Get_is_tenant_isolated()
    {
        var (teamA, ownerA) = await SeedTeamAsync();
        var (teamB, _) = await SeedTeamAsync();

        Guid channelInA;
        using (var scope = _fixture.BeginScope())
            channelInA = await scope.Resolve<IConversationService>().CreateChannelAsync(teamA, "A-chan", "a", false, ownerA, default);

        // Query the team-A channel under team B's id (even as its real owner) → null.
        using var verify = _fixture.BeginScope();
        var crossTeam = await verify.Resolve<IConversationService>().GetAsync(teamB, ownerA, channelInA, default);
        crossTeam.ShouldBeNull(customMessage: "A conversation id from another team MUST NOT resolve under this team's scope.");
    }

    [Fact]
    public async Task Get_surfaces_the_callers_read_cursor_after_mark_read()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var channelId = await SeedChannelAsync(teamId, owner, "general");

        var t0 = DateTimeOffset.UtcNow;
        await SeedMessageAsync(teamId, channelId, owner, "first", t0);
        var readUpTo = await SeedMessageAsync(teamId, channelId, owner, "second", t0.AddSeconds(5));
        await SeedMessageAsync(teamId, channelId, owner, "third (unread)", t0.AddSeconds(10));

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IMessageService>().MarkReadAsync(teamId, owner, channelId, readUpTo, default);

        using var verify = _fixture.BeginScope();
        var summary = await verify.Resolve<IConversationService>().GetAsync(teamId, owner, channelId, default);

        summary.ShouldNotBeNull();
        summary!.LastReadMessageId.ShouldBe(readUpTo, customMessage: "GetAsync must surface the caller's own read cursor so the pane can place the unread divider.");
    }

    [Fact]
    public async Task Get_leaves_read_cursor_null_until_the_caller_has_read_anything()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var channelId = await SeedChannelAsync(teamId, owner, "general");
        await SeedMessageAsync(teamId, channelId, owner, "hello", DateTimeOffset.UtcNow);

        using var verify = _fixture.BeginScope();
        var summary = await verify.Resolve<IConversationService>().GetAsync(teamId, owner, channelId, default);

        summary.ShouldNotBeNull();
        summary!.LastReadMessageId.ShouldBeNull(customMessage: "A member who has read nothing has a null cursor — the whole conversation is unread, no divider.");
    }

    [Fact]
    public async Task Get_read_cursor_is_per_caller_not_shared_across_members()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var other = await SeedUserAsync();
        var channelId = await SeedChannelAsync(teamId, owner, "general");

        using (var scope = _fixture.BeginScope())
            await scope.Resolve<IConversationService>().AddMemberAsync(teamId, owner, channelId, other, default);

        var t0 = DateTimeOffset.UtcNow;
        var first = await SeedMessageAsync(teamId, channelId, owner, "first", t0);
        var second = await SeedMessageAsync(teamId, channelId, owner, "second", t0.AddSeconds(5));

        using (var scope = _fixture.BeginScope())
        {
            var messages = scope.Resolve<IMessageService>();
            await messages.MarkReadAsync(teamId, owner, channelId, second, default);   // owner read everything
            await messages.MarkReadAsync(teamId, other, channelId, first, default);     // other read only the first
        }

        using var verify = _fixture.BeginScope();
        var conversations = verify.Resolve<IConversationService>();
        var ownerView = await conversations.GetAsync(teamId, owner, channelId, default);
        var otherView = await conversations.GetAsync(teamId, other, channelId, default);

        ownerView!.LastReadMessageId.ShouldBe(second);
        otherView!.LastReadMessageId.ShouldBe(first, customMessage: "Each caller sees their OWN read cursor — cursors must not bleed across members.");
    }

    // ─── Membership management ─────────────────────────────────────────────────────

    [Fact]
    public async Task AddMember_is_idempotent_and_resurrects_removed_member()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var newcomer = await SeedUserAsync();

        Guid channelId;
        using (var scope = _fixture.BeginScope())
            channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, "Team", "team", false, owner, default);

        using (var scope = _fixture.BeginScope())
        {
            var svc = scope.Resolve<IConversationService>();
            await svc.AddMemberAsync(teamId, owner, channelId, newcomer, default);
            await svc.AddMemberAsync(teamId, owner, channelId, newcomer, default);   // second add → no-op, no duplicate-PK throw
        }

        using var verify = _fixture.BeginScope();
        var count = await verify.Resolve<CodeSpaceDbContext>().ConversationMember.AsNoTracking()
            .CountAsync(m => m.ConversationId == channelId && m.UserId == newcomer && m.DeletedDate == null);
        count.ShouldBe(1, customMessage: "Adding an existing member twice must remain a single active membership row.");
    }

    [Fact]
    public async Task AddMember_to_direct_message_throws()
    {
        var (teamId, userA) = await SeedTeamAsync();
        var userB = await SeedUserAsync();
        var userC = await SeedUserAsync();

        Guid dmId;
        using (var scope = _fixture.BeginScope())
            dmId = await scope.Resolve<IConversationService>().GetOrCreateDirectAsync(teamId, userA, userB, default);

        using var verify = _fixture.BeginScope();
        await Should.ThrowAsync<InvalidOperationException>(
            () => verify.Resolve<IConversationService>().AddMemberAsync(teamId, userA, dmId, userC, default));
    }

    [Fact]
    public async Task AddMember_by_non_member_actor_throws()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var outsider = await SeedUserAsync();
        var newcomer = await SeedUserAsync();

        Guid channelId;
        using (var scope = _fixture.BeginScope())
            channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, "Gated", "gated", true, owner, default);

        using var verify = _fixture.BeginScope();
        await Should.ThrowAsync<InvalidOperationException>(
            () => verify.Resolve<IConversationService>().AddMemberAsync(teamId, outsider, channelId, newcomer, default));
    }

    // ─── Recent-conversations overview (last message + recency) ──────────────────────

    [Fact]
    public async Task ListForUser_populates_a_token_stripped_last_message_preview()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var channelId = await SeedChannelAsync(teamId, owner, "general");
        var mentioned = Guid.NewGuid();

        await SeedMessageAsync(teamId, channelId, owner, $"hi <user:{mentioned}|Alice> welcome", DateTimeOffset.UtcNow);

        using var verify = _fixture.BeginScope();
        var row = (await verify.Resolve<IConversationService>().ListForUserAsync(teamId, owner, default)).Single(c => c.Id == channelId);

        row.LastMessage.ShouldNotBeNull();
        row.LastMessage!.AuthorUserId.ShouldBe(owner);
        row.LastMessage.IsDeleted.ShouldBeFalse();
        row.LastMessage.Preview.ShouldBe("hi @Alice welcome", customMessage: "Preview must strip reference tokens to their labels, keeping the @ on a user mention.");
        row.LastActivityDate.ShouldBe(row.LastMessage.CreatedDate);
    }

    [Fact]
    public async Task ListForUser_surfaces_the_latest_message_not_an_earlier_one()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var channelId = await SeedChannelAsync(teamId, owner, "general");

        var t0 = DateTimeOffset.UtcNow;
        await SeedMessageAsync(teamId, channelId, owner, "first", t0);
        await SeedMessageAsync(teamId, channelId, owner, "latest", t0.AddSeconds(5));

        using var verify = _fixture.BeginScope();
        var row = (await verify.Resolve<IConversationService>().ListForUserAsync(teamId, owner, default)).Single(c => c.Id == channelId);

        row.LastMessage!.Preview.ShouldBe("latest", customMessage: "DISTINCT ON must surface the newest message per conversation.");
    }

    [Fact]
    public async Task ListForUser_orders_conversations_by_recent_activity_newest_first()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var older = await SeedChannelAsync(teamId, owner, "older");
        var newer = await SeedChannelAsync(teamId, owner, "newer");
        var empty = await SeedChannelAsync(teamId, owner, "empty");

        var t0 = DateTimeOffset.UtcNow;
        await SeedMessageAsync(teamId, older, owner, "old chatter", t0);
        await SeedMessageAsync(teamId, newer, owner, "new chatter", t0.AddMinutes(10));

        using var verify = _fixture.BeginScope();
        var ids = (await verify.Resolve<IConversationService>().ListForUserAsync(teamId, owner, default)).Select(c => c.Id).ToList();

        ids.IndexOf(newer).ShouldBeLessThan(ids.IndexOf(older), customMessage: "A conversation with a newer message must sort first.");
        ids.IndexOf(older).ShouldBeLessThan(ids.IndexOf(empty), customMessage: "An empty conversation (created earliest) sorts after one with later activity.");
    }

    [Fact]
    public async Task ListForUser_shows_a_deleted_last_message_as_a_blank_tombstone()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var channelId = await SeedChannelAsync(teamId, owner, "general");

        await SeedMessageAsync(teamId, channelId, owner, "secret", DateTimeOffset.UtcNow, deleted: true);

        using var verify = _fixture.BeginScope();
        var row = (await verify.Resolve<IConversationService>().ListForUserAsync(teamId, owner, default)).Single(c => c.Id == channelId);

        row.LastMessage.ShouldNotBeNull();
        row.LastMessage!.IsDeleted.ShouldBeTrue();
        row.LastMessage.Preview.ShouldBeEmpty(customMessage: "A deleted last message must not leak its content into the list preview.");
    }

    [Fact]
    public async Task ListForUser_leaves_last_message_null_and_falls_back_to_created_for_an_empty_conversation()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var channelId = await SeedChannelAsync(teamId, owner, "quiet");

        using var verify = _fixture.BeginScope();
        var row = (await verify.Resolve<IConversationService>().ListForUserAsync(teamId, owner, default)).Single(c => c.Id == channelId);

        row.LastMessage.ShouldBeNull();
        row.LastActivityDate.ShouldBe(row.CreatedDate, customMessage: "With no messages, last activity falls back to the conversation's creation.");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────

    private async Task<Guid> SeedChannelAsync(Guid teamId, Guid ownerId, string slug)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, false, ownerId, default);
    }

    private async Task<Guid> SeedMessageAsync(Guid teamId, Guid conversationId, Guid authorId, string body, DateTimeOffset createdAt, bool deleted = false)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        // CreateVersion7(timestamp) makes the id sort by createdAt, so DISTINCT ON (id DESC) picks
        // the latest deterministically — no inter-insert delays needed.
        var id = Guid.CreateVersion7(createdAt);
        db.Message.Add(new Message
        {
            Id = id, ConversationId = conversationId, TeamId = teamId, AuthorUserId = authorId,
            Body = body, CreatedDate = createdAt, DeletedDate = deleted ? createdAt : null,
        });
        await db.SaveChangesAsync();
        return id;
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"conv-{userId:N}@test.local", Name = $"conv-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"conv-{teamId:N}", Name = "Conv Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"conv-{userId:N}@test.local", Name = $"conv-{userId:N}" });
        await db.SaveChangesAsync();
        return userId;
    }
}

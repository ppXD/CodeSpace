using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// Proves the chat foundation schema (migration 0028) round-trips through EF and that its
/// three load-bearing performance features actually work against real Postgres:
///   1. UUID v7 message ids sort chronologically via the (conversation_id, id) index;
///   2. the generated search_tsv column + GIN index answer full-text queries;
///   3. the message_reference reverse index answers "@mentions of X" without a body scan.
///
/// This is the PR-1 (schema only) safety net — no service / API yet. Service-layer behaviour
/// (find-or-create DM, reference extraction, cursor pagination contract) lands with tests in
/// the next PR.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ChatSchemaRoundTripTests
{
    private readonly PostgresFixture _fixture;

    public ChatSchemaRoundTripTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Channel_conversation_with_members_and_messages_round_trips()
    {
        var (teamId, userId) = await SeedTeamAsync();
        var otherUserId = await SeedUserAsync();

        var conversationId = Guid.NewGuid();
        var firstMessageId = Guid.CreateVersion7();

        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            db.Conversation.Add(new Conversation
            {
                Id = conversationId,
                TeamId = teamId,
                Kind = ConversationKind.Channel,
                Slug = "general",
                Name = "General",
                Description = "Team-wide chatter",
                Visibility = ConversationVisibility.Public,
            });

            db.ConversationMember.Add(new ConversationMember
            {
                ConversationId = conversationId, UserId = userId, TeamId = teamId,
                Role = ConversationMemberRole.Owner, JoinedDate = DateTimeOffset.UtcNow,
            });
            db.ConversationMember.Add(new ConversationMember
            {
                ConversationId = conversationId, UserId = otherUserId, TeamId = teamId,
                Role = ConversationMemberRole.Member, JoinedDate = DateTimeOffset.UtcNow,
            });

            db.Message.Add(new Message
            {
                Id = firstMessageId, ConversationId = conversationId, TeamId = teamId,
                AuthorUserId = userId, Body = "first message", CreatedDate = DateTimeOffset.UtcNow,
            });

            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var vdb = verify.Resolve<CodeSpaceDbContext>();

        var conv = await vdb.Conversation.AsNoTracking().SingleAsync(c => c.Id == conversationId);
        conv.Kind.ShouldBe(ConversationKind.Channel);
        conv.Visibility.ShouldBe(ConversationVisibility.Public);
        conv.Slug.ShouldBe("general");

        var members = await vdb.ConversationMember.AsNoTracking().Where(m => m.ConversationId == conversationId).ToListAsync();
        members.Count.ShouldBe(2);
        members.ShouldContain(m => m.UserId == userId && m.Role == ConversationMemberRole.Owner);

        var msg = await vdb.Message.AsNoTracking().SingleAsync(m => m.Id == firstMessageId);
        msg.Body.ShouldBe("first message");
        msg.EditedDate.ShouldBeNull();
    }

    [Fact]
    public async Task Messages_sort_chronologically_by_uuid_v7_id()
    {
        // The performance backbone: UUID v7 ids are time-sortable, so ORDER BY id == ORDER
        // BY creation. If a future change swapped Guid.CreateVersion7() for NewGuid() (v4,
        // random), this fails — the whole cursor-pagination design relies on id order.
        var (teamId, userId) = await SeedTeamAsync();
        var conversationId = await SeedBareChannelAsync(teamId, userId);

        // Stamp each id with an EXPLICIT, strictly-increasing timestamp (1ms apart) instead of relying on
        // wall-clock spacing between calls. Guid.CreateVersion7() has no within-millisecond monotonic counter
        // (the sub-ms bits are random), so two ids minted in the same millisecond — likely under CI load —
        // sort in random relative order. Distinct millisecond timestamps make ORDER BY id deterministic while
        // still proving the invariant: a v4 NewGuid() swap ignores the timestamp → random order → still fails.
        var baseTime = DateTimeOffset.UtcNow;
        var ids = new List<Guid>();
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            for (var i = 0; i < 5; i++)
            {
                var id = Guid.CreateVersion7(baseTime.AddMilliseconds(i));
                ids.Add(id);
                db.Message.Add(new Message
                {
                    Id = id, ConversationId = conversationId, TeamId = teamId,
                    AuthorUserId = userId, Body = $"msg {i}", CreatedDate = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
            }
        }

        using var verify = _fixture.BeginScope();
        var vdb = verify.Resolve<CodeSpaceDbContext>();

        var ordered = await vdb.Message.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync();

        ordered.ShouldBe(ids, customMessage:
            "Messages MUST sort by id in insertion order. If they don't, the id is no longer " +
            "time-sortable (UUID v7) — cursor pagination + chronological rendering break.");
    }

    [Fact]
    public async Task Generated_tsvector_answers_full_text_search()
    {
        // search_tsv is a generated column — the app never writes it, Postgres derives it
        // from body on insert. Prove a GIN-indexed full-text query finds the right message
        // and excludes non-matches, end-to-end against real PG.
        var (teamId, userId) = await SeedTeamAsync();
        var conversationId = await SeedBareChannelAsync(teamId, userId);

        var matchId = Guid.CreateVersion7();
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();
            db.Message.Add(new Message { Id = matchId, ConversationId = conversationId, TeamId = teamId, AuthorUserId = userId, Body = "the retry logic needs a backoff", CreatedDate = DateTimeOffset.UtcNow });
            db.Message.Add(new Message { Id = Guid.CreateVersion7(), ConversationId = conversationId, TeamId = teamId, AuthorUserId = userId, Body = "unrelated chatter about lunch", CreatedDate = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var vdb = verify.Resolve<CodeSpaceDbContext>();

        // FromSqlInterpolated parameterises the search term (no SQL injection); the WHERE
        // hits the GIN index on search_tsv.
        var hits = await vdb.Message
            .FromSqlInterpolated($"SELECT * FROM message WHERE conversation_id = {conversationId} AND search_tsv @@ to_tsquery('simple', 'retry')")
            .AsNoTracking()
            .ToListAsync();

        hits.Count.ShouldBe(1, customMessage: "FTS should match exactly the message containing 'retry'.");
        hits[0].Id.ShouldBe(matchId);
    }

    [Fact]
    public async Task Message_reference_reverse_lookup_finds_all_mentions_of_a_target()
    {
        // The generic @ system's payoff: "every message that references (ref_type, ref_id)".
        // Seed two messages mentioning the same PR + one mentioning a user, then prove the
        // reverse index returns exactly the PR mentions for this team.
        var (teamId, userId) = await SeedTeamAsync();
        var conversationId = await SeedBareChannelAsync(teamId, userId);

        const string prRefId = "repo-abc#123";
        using (var scope = _fixture.BeginScope())
        {
            var db = scope.Resolve<CodeSpaceDbContext>();

            var m1 = Guid.CreateVersion7();
            var m2 = Guid.CreateVersion7();
            var m3 = Guid.CreateVersion7();
            db.Message.AddRange(
                new Message { Id = m1, ConversationId = conversationId, TeamId = teamId, AuthorUserId = userId, Body = "look at @pr", CreatedDate = DateTimeOffset.UtcNow },
                new Message { Id = m2, ConversationId = conversationId, TeamId = teamId, AuthorUserId = userId, Body = "still @pr", CreatedDate = DateTimeOffset.UtcNow },
                new Message { Id = m3, ConversationId = conversationId, TeamId = teamId, AuthorUserId = userId, Body = "hey @alice", CreatedDate = DateTimeOffset.UtcNow });

            db.MessageReference.AddRange(
                new MessageReference { Id = Guid.NewGuid(), MessageId = m1, TeamId = teamId, RefType = "pull_request", RefId = prRefId, RefMetadataJson = """{"label":"#123 Fix retry"}""", CreatedDate = DateTimeOffset.UtcNow },
                new MessageReference { Id = Guid.NewGuid(), MessageId = m2, TeamId = teamId, RefType = "pull_request", RefId = prRefId, CreatedDate = DateTimeOffset.UtcNow },
                new MessageReference { Id = Guid.NewGuid(), MessageId = m3, TeamId = teamId, RefType = "user", RefId = userId.ToString(), CreatedDate = DateTimeOffset.UtcNow });

            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var vdb = verify.Resolve<CodeSpaceDbContext>();

        var prMentions = await vdb.MessageReference.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.RefType == "pull_request" && r.RefId == prRefId)
            .ToListAsync();

        prMentions.Count.ShouldBe(2, customMessage:
            "Reverse lookup MUST return exactly the messages referencing this PR — the backlink / mention-inbox query.");
        prMentions.ShouldContain(r => r.RefMetadataJson != null && r.RefMetadataJson.Contains("Fix retry"),
            customMessage: "jsonb ref_metadata MUST round-trip so the chip renderer can read the cached label.");
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"chat-{userId:N}@test.local", Name = $"chat-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"chat-{teamId:N}", Name = "Chat Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"chat-{userId:N}@test.local", Name = $"chat-{userId:N}" });
        await db.SaveChangesAsync();
        return userId;
    }

    private async Task<Guid> SeedBareChannelAsync(Guid teamId, Guid ownerUserId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var conversationId = Guid.NewGuid();
        db.Conversation.Add(new Conversation
        {
            Id = conversationId, TeamId = teamId, Kind = ConversationKind.Channel,
            Slug = $"c-{conversationId:N}".Substring(0, 20), Name = "Chan", Visibility = ConversationVisibility.Public,
        });
        db.ConversationMember.Add(new ConversationMember
        {
            ConversationId = conversationId, UserId = ownerUserId, TeamId = teamId,
            Role = ConversationMemberRole.Owner, JoinedDate = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return conversationId;
    }
}

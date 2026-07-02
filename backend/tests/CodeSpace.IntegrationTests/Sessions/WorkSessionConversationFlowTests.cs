using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Sessions;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Sessions;

/// <summary>
/// The session chat surface's get-or-stage semantics over real Postgres (S4a): one channel per thread, adopt
/// on re-ensure, and — the review-hardened piece — a DIFFERENT team member continuing the thread is staged
/// into the room's membership (the staging path adds only the creator as Owner; without the upsert their
/// cards would post into a channel they cannot see).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class WorkSessionConversationFlowTests
{
    private readonly PostgresFixture _fixture;

    public WorkSessionConversationFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task A_second_actor_reuses_the_channel_and_is_staged_into_its_membership()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var otherId = await SeedUserAsync();

        Guid sessionId, channelId;
        using (var scope = _fixture.BeginScope())
        {
            var sessions = scope.Resolve<IWorkSessionService>();
            var db = scope.Resolve<CodeSpaceDbContext>();

            sessionId = (await sessions.OpenAsync(teamId, "Ship the feature", WorkSessionKind.Task, ownerId, CancellationToken.None)).SessionId;
            channelId = await sessions.EnsureConversationAsync(sessionId, teamId, ownerId, CancellationToken.None);

            await db.SaveChangesAsync();
        }

        using (var scope = _fixture.BeginScope())
        {
            var sessions = scope.Resolve<IWorkSessionService>();
            var db = scope.Resolve<CodeSpaceDbContext>();

            var reused = await sessions.EnsureConversationAsync(sessionId, teamId, otherId, CancellationToken.None);
            reused.ShouldBe(channelId, "one thread, one room — the second actor adopts the linked channel");

            await db.SaveChangesAsync();
        }

        using var verify = _fixture.BeginScope();
        var vdb = verify.Resolve<CodeSpaceDbContext>();

        (await vdb.Conversation.AsNoTracking().CountAsync(c => c.TeamId == teamId)).ShouldBe(1, "no twin channel was minted");

        var members = await vdb.ConversationMember.AsNoTracking().Where(m => m.ConversationId == channelId).ToListAsync();
        members.Select(m => m.UserId).ShouldBe(new[] { ownerId, otherId }, ignoreOrder: true,
            customMessage: "the creator owns the room AND the second actor was upserted — their cards are visible to both");

        // Idempotence: a third ensure by the same actor stages no duplicate membership.
        using (var scope = _fixture.BeginScope())
        {
            await scope.Resolve<IWorkSessionService>().EnsureConversationAsync(sessionId, teamId, otherId, CancellationToken.None);
            await scope.Resolve<CodeSpaceDbContext>().SaveChangesAsync();
        }

        (await vdb.ConversationMember.AsNoTracking().CountAsync(m => m.ConversationId == channelId)).ShouldBe(2);
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"sess-{userId:N}@test.local", Name = $"sess-{userId:N}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();

        return userId;
    }
}

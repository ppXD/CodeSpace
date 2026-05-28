using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Chat;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Chat;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// End-to-end through the MediatR pipeline — the path the controller drives. Proves the thin
/// message handlers (Rule 16) thread <c>ICurrentTeam</c> + <c>ICurrentUser</c> into the service
/// so a post lands as the caller, a list is membership-scoped, and edit / delete / mark-read
/// route correctly. The service's own logic is exhaustively covered in
/// <see cref="MessageServiceFlowTests"/>; this is the wiring contract.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class MessageApiFlowTests
{
    private readonly PostgresFixture _fixture;

    public MessageApiFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task PostMessage_then_List_round_trips_through_mediator_with_references()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid channelId, messageId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            channelId = await mediator.Send(new CreateChannelCommand { Name = "General", Slug = "general" });

            var posted = await mediator.Send(new PostMessageCommand { ConversationId = channelId, Body = $"hi <user:{userId}|Me>" });
            messageId = posted.Id;
            posted.References.ShouldHaveSingleItem().RefType.ShouldBe("user");
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var page = await verify.Resolve<IMediator>().Send(new ListMessagesQuery { ConversationId = channelId });

        page.Messages.ShouldContain(m => m.Id == messageId && m.Body.Contains("hi"));
    }

    [Fact]
    public async Task EditMessage_through_mediator_updates_body_and_marks_edited()
    {
        var (teamId, userId) = await SeedTeamAsync();

        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var mediator = scope.Resolve<IMediator>();

        var channelId = await mediator.Send(new CreateChannelCommand { Name = "General", Slug = "general" });
        var posted = await mediator.Send(new PostMessageCommand { ConversationId = channelId, Body = "v1" });

        var edited = await mediator.Send(new EditMessageCommand { MessageId = posted.Id, Body = "v2" });

        edited.Body.ShouldBe("v2");
        edited.EditedDate.ShouldNotBeNull();
    }

    [Fact]
    public async Task DeleteMessage_through_mediator_renders_tombstone_in_list()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid channelId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            channelId = await mediator.Send(new CreateChannelCommand { Name = "General", Slug = "general" });
            var posted = await mediator.Send(new PostMessageCommand { ConversationId = channelId, Body = "to delete" });
            await mediator.Send(new DeleteMessageCommand { MessageId = posted.Id });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var page = await verify.Resolve<IMediator>().Send(new ListMessagesQuery { ConversationId = channelId });

        var tomb = page.Messages.ShouldHaveSingleItem();
        tomb.IsDeleted.ShouldBeTrue();
        tomb.Body.ShouldBeEmpty();
    }

    [Fact]
    public async Task MarkConversationRead_through_mediator_advances_the_cursor()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid channelId, messageId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            channelId = await mediator.Send(new CreateChannelCommand { Name = "General", Slug = "general" });
            var posted = await mediator.Send(new PostMessageCommand { ConversationId = channelId, Body = "read me" });
            messageId = posted.Id;

            await mediator.Send(new MarkConversationReadCommand { ConversationId = channelId, LastReadMessageId = messageId });
        }

        using var verify = _fixture.BeginScope();
        var cursor = await verify.Resolve<CodeSpaceDbContext>().ConversationMember.AsNoTracking()
            .Where(m => m.ConversationId == channelId && m.UserId == userId)
            .Select(m => m.LastReadMessageId)
            .SingleAsync();

        cursor.ShouldBe(messageId);
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"mapi-{userId:N}@test.local", Name = $"mapi-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"mapi-{teamId:N}", Name = "Msg API Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }
}

using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Commands.Chat;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Chat;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// End-to-end through the MediatR pipeline — the path the controller actually drives. Proves
/// the thin command/query handlers (Rule 16) correctly thread <c>ICurrentTeam</c> +
/// <c>ICurrentUser</c> into the service, so a create lands under the caller's team and a list
/// returns only that caller's conversations. The service's own logic is exhaustively covered
/// in <see cref="ConversationServiceFlowTests"/>; this is the wiring contract.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ConversationApiFlowTests
{
    private readonly PostgresFixture _fixture;

    public ConversationApiFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task CreateChannel_then_List_round_trips_through_mediator_scoped_to_caller()
    {
        var (teamId, userId) = await SeedTeamAsync();

        Guid channelId;
        using (var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin))
        {
            var mediator = scope.Resolve<IMediator>();
            channelId = await mediator.Send(new CreateChannelCommand { Name = "General", Slug = "general" });
        }

        using var verify = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        var list = await verify.Resolve<IMediator>().Send(new ListConversationsQuery());

        list.ShouldContain(c => c.Id == channelId && c.Kind == ConversationKind.Channel && c.Slug == "general");
    }

    [Fact]
    public async Task OpenDirect_then_Get_returns_summary_for_a_member()
    {
        var (teamId, userA) = await SeedTeamAsync();
        var userB = await SeedUserAsync();

        Guid dmId;
        using (var scope = _fixture.BeginScopeAs(userA, teamId, Roles.Admin))
            dmId = await scope.Resolve<IMediator>().Send(new OpenDirectConversationCommand { OtherUserId = userB });

        using var verify = _fixture.BeginScopeAs(userA, teamId, Roles.Admin);
        var summary = await verify.Resolve<IMediator>().Send(new GetConversationQuery { ConversationId = dmId });

        summary.ShouldNotBeNull();
        summary!.Kind.ShouldBe(ConversationKind.Direct);
        summary.MemberUserIds.ShouldContain(userA);
        summary.MemberUserIds.ShouldContain(userB);
    }

    [Fact]
    public async Task Get_returns_null_through_mediator_for_a_non_member()
    {
        var (teamId, owner) = await SeedTeamAsync();
        var outsider = await SeedUserAsync();

        Guid channelId;
        using (var scope = _fixture.BeginScopeAs(owner, teamId, Roles.Admin))
            channelId = await scope.Resolve<IMediator>().Send(new CreateChannelCommand { Name = "Gated", Slug = "gated", Private = true });

        using var verify = _fixture.BeginScopeAs(outsider, teamId, Roles.Admin);
        var summary = await verify.Resolve<IMediator>().Send(new GetConversationQuery { ConversationId = channelId });

        summary.ShouldBeNull(customMessage: "Even through the mediator path, a non-member must get null (controller → 404).");
    }

    private async Task<(Guid TeamId, Guid UserId)> SeedTeamAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();

        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"capi-{userId:N}@test.local", Name = $"capi-{userId:N}" });

        var teamId = Guid.NewGuid();
        db.Team.Add(new Team { Id = teamId, Slug = $"capi-{teamId:N}", Name = "Conv API Team", Kind = TeamKind.Workspace, OwnerUserId = userId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner });

        await db.SaveChangesAsync();
        return (teamId, userId);
    }

    private async Task<Guid> SeedUserAsync()
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        db.User.Add(new User { Id = userId, Email = $"capi-{userId:N}@test.local", Name = $"capi-{userId:N}" });
        await db.SaveChangesAsync();
        return userId;
    }
}

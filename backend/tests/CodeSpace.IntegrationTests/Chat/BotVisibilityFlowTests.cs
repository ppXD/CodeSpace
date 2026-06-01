using Autofac;
using CodeSpace.Core.Services.Chat;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Users;
using CodeSpace.Messages.Queries.Users;
using MediatR;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// The generic "default-exclude bots" backbone, end-to-end through the real MediatR pipeline + EF
/// global query filter. The SAME team + the SAME service method back both queries; only the
/// <c>IBotInclusive</c> marker on the identities query differs. This proves a new query can't leak
/// the bot by default (it's filtered out unless the request type explicitly opts in).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class BotVisibilityFlowTests
{
    private readonly PostgresFixture _fixture;

    public BotVisibilityFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task Default_member_list_excludes_the_bot_while_identities_include_it()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        Guid botId;
        using (var scope = _fixture.BeginScope())
            botId = await scope.Resolve<IChatBotService>().GetOrCreateTeamBotAsync(teamId, default);

        var members = await SendAsync(ownerId, teamId, new ListTeamMembersQuery());
        members.ShouldContain(m => m.UserId == ownerId, "the human owner is a normal team member");
        members.ShouldNotContain(m => m.UserId == botId, "the default member list / @-mention picker must NOT leak the bot");
        members.ShouldAllBe(m => !m.IsBot);

        var identities = await SendAsync(ownerId, teamId, new ListTeamMemberIdentitiesQuery());
        identities.ShouldContain(m => m.UserId == botId && m.IsBot, "author-name resolution (IBotInclusive) must see the bot");
        identities.ShouldContain(m => m.UserId == ownerId, "humans are still present in the identities list");
    }

    private async Task<IReadOnlyList<TeamMemberSummary>> SendAsync(Guid userId, Guid teamId, IRequest<IReadOnlyList<TeamMemberSummary>> query)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(query);
    }
}

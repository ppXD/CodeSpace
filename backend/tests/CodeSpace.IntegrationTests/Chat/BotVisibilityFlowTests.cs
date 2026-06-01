using Autofac;
using CodeSpace.Core.Services.Chat;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Chat;
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

    [Fact]
    public async Task Conversation_member_count_and_roster_exclude_the_bot()
    {
        // A ConversationMember enumeration (count + id list) is queried DIRECTLY, not through a User
        // join — so a User-only filter would miss it. This pins that the bot stays out anyway, via the
        // `_db.User.Any(...)` correlation that rides the global filter.
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var slug = "vis-" + Guid.NewGuid().ToString("N")[..8];
        Guid channelId;
        using (var scope = _fixture.BeginScope())
            channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, default);

        Guid botId;
        using (var scope = _fixture.BeginScope())
        {
            var bot = scope.Resolve<IChatBotService>();
            await bot.PostAsBotAsync(channelId, "deployed ✅", interaction: null, default);   // the bot auto-joins the channel
            botId = await bot.GetOrCreateTeamBotAsync(teamId, default);
        }

        IReadOnlyList<ConversationSummary> conversations;
        using (var scope = _fixture.BeginScope())
            conversations = await scope.Resolve<IConversationService>().ListForUserAsync(teamId, ownerId, default);

        var channel = conversations.Single(c => c.Id == channelId);
        channel.MemberUserIds.ShouldNotContain(botId, "the bot must not leak into the conversation roster");
        channel.MemberUserIds.ShouldContain(ownerId);
        channel.MemberCount.ShouldBe(1, "the member count is humans-only — the bot member must not inflate it");
    }

    private async Task<IReadOnlyList<TeamMemberSummary>> SendAsync(Guid userId, Guid teamId, IRequest<IReadOnlyList<TeamMemberSummary>> query)
    {
        using var scope = _fixture.BeginScopeAs(userId, teamId, Roles.Admin);
        return await scope.Resolve<IMediator>().Send(query);
    }
}

using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Services.Chat;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Dtos.Chat;
using CodeSpace.Messages.Dtos.Chat.Interactions;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.IntegrationTests.Chat;

/// <summary>
/// <see cref="IChatBotService"/> against real Postgres — the per-team CodeSpace bot that lets a
/// workflow post into chat with no human actor. Pins: get-or-create is idempotent + race-safe +
/// per-team distinct; PostAsBot derives the team from the conversation, auto-joins the bot, and
/// persists the message (plain + interactive) authored by the bot.
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class ChatBotServiceFlowTests
{
    private readonly PostgresFixture _fixture;

    public ChatBotServiceFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    [Fact]
    public async Task GetOrCreateTeamBot_is_idempotent_and_creates_exactly_one_bot()
    {
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var first = await GetOrCreateBotAsync(teamId);
        var second = await GetOrCreateBotAsync(teamId);

        second.ShouldBe(first, "the team bot is get-or-create, not create-every-time");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        // This scope didn't opt into bot visibility, so bypass the global filter to inspect the bot row.
        var bots = await db.User.AsNoTracking().IgnoreQueryFilters()
            .Where(u => u.IsBot && db.TeamMembership.Any(m => m.UserId == u.Id && m.TeamId == teamId))
            .ToListAsync();

        bots.ShouldHaveSingleItem().Name.ShouldBe(ChatBotService.BotDisplayName);
    }

    [Fact]
    public async Task GetOrCreateTeamBot_is_distinct_per_team()
    {
        var (teamA, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);
        var (teamB, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        (await GetOrCreateBotAsync(teamA)).ShouldNotBe(await GetOrCreateBotAsync(teamB), "each team gets its own bot identity");
    }

    [Fact]
    public async Task Concurrent_get_or_create_resolves_to_a_single_bot()
    {
        // Two parallel first-posts for the same team must not create two bots — the deterministic
        // bot email on the app_user unique index makes one win; the loser re-reads it.
        var (teamId, _) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        var ids = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => GetOrCreateBotAsync(teamId)));

        ids.Distinct().Count().ShouldBe(1, "every racing caller must resolve to the same bot id");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();
        var count = await db.User.AsNoTracking().IgnoreQueryFilters()
            .CountAsync(u => u.IsBot && db.TeamMembership.Any(m => m.UserId == u.Id && m.TeamId == teamId));
        count.ShouldBe(1, "the unique-email race guard must prevent a duplicate bot row");
    }

    [Fact]
    public async Task PostAsBot_auto_joins_the_conversation_and_authors_as_the_bot()
    {
        var (teamId, ownerId, channelId) = await SeedChannelAsync();

        var view = await PostAsBotAsync(channelId, "Deployment finished ✅", interaction: null);

        view.IsDeleted.ShouldBeFalse();
        view.AuthorUserId.ShouldNotBe(ownerId, "the message is authored by the bot, not the channel owner");

        using var verify = _fixture.BeginScope();
        var db = verify.Resolve<CodeSpaceDbContext>();

        // The author IS the bot, which the global filter hides — bypass it to verify the identity.
        var author = await db.User.AsNoTracking().IgnoreQueryFilters().SingleAsync(u => u.Id == view.AuthorUserId);
        author.IsBot.ShouldBeTrue();
        author.Name.ShouldBe(ChatBotService.BotDisplayName);

        var botIsMember = await db.ConversationMember.AsNoTracking()
            .AnyAsync(m => m.ConversationId == channelId && m.UserId == view.AuthorUserId && m.DeletedDate == null);
        botIsMember.ShouldBeTrue("the bot auto-joined the conversation so the post passed the membership gate");
    }

    [Fact]
    public async Task PostAsBot_persists_an_interactive_card()
    {
        var (_, _, channelId) = await SeedChannelAsync();

        var interaction = new MessageInteraction
        {
            Component = new ActionButtonsComponent
            {
                Buttons = new List<InteractionButton> { new() { Key = "approve", Label = "Approve", Style = InteractionButtonStyle.Primary } },
            },
            Target = new WorkflowWaitTarget { Token = "tok-bot-card" },
        };

        var view = await PostAsBotAsync(channelId, "Review PR #7?", interaction);

        view.Interaction.ShouldNotBeNull().Component.ShouldBeOfType<ActionButtonsComponent>()
            .Buttons.ShouldHaveSingleItem().Key.ShouldBe("approve");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> GetOrCreateBotAsync(Guid teamId)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IChatBotService>().GetOrCreateTeamBotAsync(teamId, default);
    }

    private async Task<MessageView> PostAsBotAsync(Guid conversationId, string body, MessageInteraction? interaction)
    {
        using var scope = _fixture.BeginScope();
        return await scope.Resolve<IChatBotService>().PostAsBotAsync(conversationId, body, interaction, default);
    }

    private async Task<(Guid TeamId, Guid OwnerId, Guid ChannelId)> SeedChannelAsync()
    {
        var (teamId, ownerId) = await WorkflowsTestSeed.SeedTeamAsync(_fixture);

        using var scope = _fixture.BeginScope();
        var slug = "bot-" + Guid.NewGuid().ToString("N")[..8];
        var channelId = await scope.Resolve<IConversationService>().CreateChannelAsync(teamId, slug, slug, isPrivate: false, ownerId, default);

        return (teamId, ownerId, channelId);
    }
}

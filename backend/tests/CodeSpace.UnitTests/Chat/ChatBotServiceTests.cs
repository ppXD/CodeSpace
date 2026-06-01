using CodeSpace.Core.Services.Chat;
using Shouldly;

namespace CodeSpace.UnitTests.Chat;

/// <summary>
/// The bot's email is the race-safe get-or-create key (it rides the app_user unique-email index),
/// so it MUST be deterministic per team and stable across releases — a rename would orphan every
/// existing team bot and re-create a fresh one. Pin the format + display name (Rule 8 spirit).
/// </summary>
[Trait("Category", "Unit")]
public class ChatBotServiceTests
{
    [Fact]
    public void Bot_email_is_deterministic_and_pinned_per_team()
    {
        var teamId = Guid.Parse("11111111-1111-1111-1111-111111111111");

        ChatBotService.BotEmail(teamId).ShouldBe("codespace-bot.11111111111111111111111111111111@bot.codespace.local");
    }

    [Fact]
    public void Bot_email_differs_per_team()
    {
        ChatBotService.BotEmail(Guid.Parse("11111111-1111-1111-1111-111111111111"))
            .ShouldNotBe(ChatBotService.BotEmail(Guid.Parse("22222222-2222-2222-2222-222222222222")));
    }

    [Fact]
    public void Bot_display_name_is_pinned()
    {
        ChatBotService.BotDisplayName.ShouldBe("CodeSpace");
    }
}

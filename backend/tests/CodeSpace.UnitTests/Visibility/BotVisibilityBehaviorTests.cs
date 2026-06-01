using CodeSpace.Core.Middlewares.Visibility;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Messages.Visibility;
using MediatR;
using Shouldly;

namespace CodeSpace.UnitTests.Visibility;

/// <summary>
/// The behavior is the opt-IN half of the default-exclude-bots scheme: it flips the request-scoped
/// <see cref="IBotVisibility"/> ON only for <see cref="IBotInclusive"/> requests, so a plain request
/// runs bot-free. The EF global filter (integration-tested) is the enforcement half.
/// </summary>
[Trait("Category", "Unit")]
public class BotVisibilityBehaviorTests
{
    private sealed record BotInclusiveRequest : IRequest<int>, IBotInclusive;
    private sealed record PlainRequest : IRequest<int>;

    [Fact]
    public async Task Turns_on_bot_visibility_for_a_bot_inclusive_request()
    {
        var visibility = new BotVisibility();
        var behavior = new BotVisibilityBehavior<BotInclusiveRequest, int>(visibility);

        await behavior.Handle(new BotInclusiveRequest(), _ => Task.FromResult(1), default);

        visibility.IncludeBots.ShouldBeTrue();
    }

    [Fact]
    public async Task Leaves_bot_visibility_off_for_a_plain_request()
    {
        var visibility = new BotVisibility();
        var behavior = new BotVisibilityBehavior<PlainRequest, int>(visibility);

        await behavior.Handle(new PlainRequest(), _ => Task.FromResult(1), default);

        visibility.IncludeBots.ShouldBeFalse("a request that doesn't opt in must never see bots — the default is exclude");
    }

    [Fact]
    public async Task Passes_through_to_the_next_handler()
    {
        var behavior = new BotVisibilityBehavior<PlainRequest, int>(new BotVisibility());

        var result = await behavior.Handle(new PlainRequest(), _ => Task.FromResult(42), default);

        result.ShouldBe(42);
    }
}

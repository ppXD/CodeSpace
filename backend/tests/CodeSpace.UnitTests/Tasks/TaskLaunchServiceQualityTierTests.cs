using CodeSpace.Core.Services.Tasks;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins <see cref="TaskLaunchService.BuildAgentProfile"/>'s tier-aware <c>OutputReviewMode</c> floor (Delivery ⇒ at
/// least Gate, Unattended ⇒ at least Improve — a MINIMUM an operator's explicit choice can only raise, never lower). The
/// tier's OTHER mandate — an executable acceptance floor on every route that grades one — is a launch-control disposition
/// now, pinned lane by lane in <c>LaunchControlResolverTests</c> (the preview predicts it; the launch refuses on it).
/// </summary>
[Trait("Category", "Unit")]
public class TaskLaunchServiceQualityTierTests
{
    private static readonly TaskLaunchSeed Seed = new() { Goal = "do the thing", SurfaceKind = "chat", TeamId = Guid.NewGuid() };

    private static RoutePlan Route(string projectionKind) => new() { ProjectionKind = projectionKind, Caps = new RouteCaps() };

    private static TaskLaunchRequest Request(QualityTier? tier) => new()
    {
        TeamId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        SurfaceKind = "chat",
        Tier = tier,
    };

    // ── BuildAgentProfile's tier-aware OutputReviewMode floor ──

    [Theory]
    [InlineData(null, ReviewMode.None)]
    [InlineData(QualityTier.Prototype, ReviewMode.None)]
    [InlineData(QualityTier.Delivery, ReviewMode.Gate)]
    [InlineData(QualityTier.Unattended, ReviewMode.Improve)]
    public void BuildAgentProfile_raises_an_unset_output_review_mode_to_the_tiers_floor(QualityTier? tier, ReviewMode expected)
    {
        var profile = TaskLaunchService.BuildAgentProfile(Request(tier), Seed, Route(TaskProjectionKinds.SingleAgent));

        profile.OutputReviewMode.ShouldBe(expected);
    }

    [Fact]
    public void BuildAgentProfile_lets_an_operators_higher_choice_win_over_the_tiers_floor()
    {
        var request = Request(QualityTier.Delivery) with { Overrides = new TaskExecutionOverrides { OutputReviewMode = ReviewMode.Improve } };

        var profile = TaskLaunchService.BuildAgentProfile(request, Seed, Route(TaskProjectionKinds.SingleAgent));

        profile.OutputReviewMode.ShouldBe(ReviewMode.Improve, "an explicit operator choice above the tier's floor is never downgraded");
    }

    [Fact]
    public void BuildAgentProfile_raises_an_operators_lower_choice_up_to_the_tiers_floor()
    {
        // The floor is a MINIMUM the mandate enforces — Unattended cannot be silently downgraded to Gate by an
        // operator override, or the whole point of "mandatory" evaporates.
        var request = Request(QualityTier.Unattended) with { Overrides = new TaskExecutionOverrides { OutputReviewMode = ReviewMode.Gate } };

        var profile = TaskLaunchService.BuildAgentProfile(request, Seed, Route(TaskProjectionKinds.SingleAgent));

        profile.OutputReviewMode.ShouldBe(ReviewMode.Improve, "Unattended's floor cannot be bypassed by requesting a lower mode");
    }
}

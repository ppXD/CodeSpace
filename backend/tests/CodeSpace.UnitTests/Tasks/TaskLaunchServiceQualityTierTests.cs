using CodeSpace.Core.Services.Tasks;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins P3.2's two tier-mandate choke points: <see cref="TaskLaunchService.EnsureAcceptanceMandate"/> (Delivery/
/// Unattended on a SUPERVISOR-projected launch must carry an executable <c>acceptanceChecks</c> floor, fail-loud
/// otherwise) and <see cref="TaskLaunchService.BuildAgentProfile"/>'s tier-aware <c>OutputReviewMode</c> floor
/// (Delivery ⇒ at least Gate, Unattended ⇒ at least Improve — a MINIMUM an operator's explicit choice can only
/// raise, never lower). Both are pure, so they're unit-pinned directly here (no DB) — the integration tier proves
/// a Delivery launch without an acceptance check is rejected through the REAL <c>ITaskLaunchService</c>.
/// </summary>
[Trait("Category", "Unit")]
public class TaskLaunchServiceQualityTierTests
{
    private static readonly TaskLaunchSeed Seed = new() { Goal = "do the thing", SurfaceKind = "chat", TeamId = Guid.NewGuid() };

    private static RoutePlan Route(string projectionKind) => new() { ProjectionKind = projectionKind, Caps = new RouteCaps() };

    private static TaskLaunchRequest Request(QualityTier? tier, IReadOnlyList<string>? acceptanceChecks = null) => new()
    {
        TeamId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        SurfaceKind = "chat",
        Tier = tier,
        AcceptanceChecks = acceptanceChecks,
    };

    // ── EnsureAcceptanceMandate — Delivery/Unattended on a supervisor launch must carry an acceptance floor ──

    [Theory]
    [InlineData(QualityTier.Delivery)]
    [InlineData(QualityTier.Unattended)]
    public void A_supervisor_launch_at_delivery_or_unattended_quality_without_an_acceptance_check_is_rejected(QualityTier tier)
    {
        var ex = Should.Throw<ArgumentException>(() =>
            TaskLaunchService.EnsureAcceptanceMandate(Request(tier), Route(TaskProjectionKinds.Supervisor)));

        ex.Message.ShouldContain("acceptanceChecks", Case.Insensitive, "the operator needs an actionable name for the missing lever");
        ex.Message.ShouldContain(tier.ToString());
    }

    [Theory]
    [InlineData(QualityTier.Delivery)]
    [InlineData(QualityTier.Unattended)]
    public void A_supervisor_launch_with_an_authored_acceptance_check_is_not_rejected(QualityTier tier)
    {
        Should.NotThrow(() =>
            TaskLaunchService.EnsureAcceptanceMandate(Request(tier, new[] { "sh", "check.sh" }), Route(TaskProjectionKinds.Supervisor)));
    }

    [Fact]
    public void A_supervisor_launch_at_prototype_quality_or_no_tier_is_never_rejected()
    {
        Should.NotThrow(() => TaskLaunchService.EnsureAcceptanceMandate(Request(QualityTier.Prototype), Route(TaskProjectionKinds.Supervisor)));
        Should.NotThrow(() => TaskLaunchService.EnsureAcceptanceMandate(Request(tier: null), Route(TaskProjectionKinds.Supervisor)));
    }

    [Theory]
    [InlineData(QualityTier.Delivery)]
    [InlineData(QualityTier.Unattended)]
    public void A_non_supervisor_launch_at_delivery_or_unattended_quality_is_never_rejected(QualityTier tier)
    {
        // AcceptanceChecks is inert on a non-supervisor projection today — this PR doesn't invent new acceptance-floor
        // plumbing for single-agent/plan-map launches, so the mandate is inert there too, matching that existing shape.
        Should.NotThrow(() => TaskLaunchService.EnsureAcceptanceMandate(Request(tier), Route(TaskProjectionKinds.SingleAgent)));
        Should.NotThrow(() => TaskLaunchService.EnsureAcceptanceMandate(Request(tier), Route(TaskProjectionKinds.PlanMapDynamic)));
    }

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

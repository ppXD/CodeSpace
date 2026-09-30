using CodeSpace.Core.Services.Tasks;
using CodeSpace.Core.Services.Tasks.Projection;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapDynamic;
using CodeSpace.Core.Services.Tasks.Projection.Builders.PlanMapSynth;
using CodeSpace.Core.Services.Tasks.Projection.Builders.SingleAgent;
using CodeSpace.Core.Services.Tasks.Projection.Builders.Supervisor;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins P3.2's two tier-mandate choke points: <see cref="TaskLaunchService.EnsureAcceptanceMandate"/> (Delivery/
/// Unattended on a launch whose route GRADES an operator floor — the supervisor and the single agent both do — must
/// carry an executable <c>acceptanceChecks</c> floor, fail-loud otherwise) and <see cref="TaskLaunchService.BuildAgentProfile"/>'s
/// tier-aware <c>OutputReviewMode</c> floor (Delivery ⇒ at least Gate, Unattended ⇒ at least Improve — a MINIMUM an
/// operator's explicit choice can only raise, never lower). Both are pure, so they're unit-pinned directly here (no
/// DB) — the integration tier proves a Delivery launch without an acceptance check is rejected through the REAL
/// <c>ITaskLaunchService</c>. "Grades a floor" is read the way the launch reads it: the REAL builder's own advertisement.
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

    // ── EnsureAcceptanceMandate — Delivery/Unattended on a route that grades a floor must carry one ──

    /// <summary>Whether the builder's route grades an operator floor — the SAME advertisement the launch's control resolution reads.</summary>
    private static bool GradesFloor(IWorkflowDefinitionBuilder builder) => builder.OperatorAcceptance.AcceptsCommand == true;

    public static IEnumerable<object[]> FloorGradingRoutesAtMandatedTiers()
    {
        foreach (var tier in new[] { QualityTier.Delivery, QualityTier.Unattended })
        {
            yield return new object[] { tier, TaskProjectionKinds.Supervisor };
            yield return new object[] { tier, TaskProjectionKinds.SingleAgent };
        }
    }

    private static IWorkflowDefinitionBuilder Builder(string projectionKind) => projectionKind switch
    {
        TaskProjectionKinds.Supervisor => new SupervisorDefinitionBuilder(),
        TaskProjectionKinds.SingleAgent => new SingleAgentDefinitionBuilder(),
        TaskProjectionKinds.PlanMapSynth => new PlanMapSynthDefinitionBuilder(),
        _ => new PlanMapDynamicDefinitionBuilder(),
    };

    [Theory]
    [MemberData(nameof(FloorGradingRoutesAtMandatedTiers))]
    public void A_launch_whose_route_grades_a_floor_at_delivery_or_unattended_quality_without_an_acceptance_check_is_rejected(QualityTier tier, string projectionKind)
    {
        // Quick grades its single agent with the operator's argv exactly as Deep grades its terminal stop, so claiming
        // Delivery there without one is the same unverified claim — it used to launch because the mandate asked
        // "is this the supervisor?" rather than "does this route grade a floor?".
        var ex = Should.Throw<ArgumentException>(() =>
            TaskLaunchService.EnsureAcceptanceMandate(Request(tier), GradesFloor(Builder(projectionKind))));

        ex.Message.ShouldContain("acceptanceChecks", Case.Insensitive, "the operator needs an actionable name for the missing lever");
        ex.Message.ShouldContain(tier.ToString());
    }

    [Theory]
    [MemberData(nameof(FloorGradingRoutesAtMandatedTiers))]
    public void A_launch_with_an_authored_acceptance_check_is_not_rejected(QualityTier tier, string projectionKind)
    {
        Should.NotThrow(() =>
            TaskLaunchService.EnsureAcceptanceMandate(Request(tier, new[] { "sh", "check.sh" }), GradesFloor(Builder(projectionKind))));
    }

    [Fact]
    public void A_launch_at_prototype_quality_or_no_tier_is_never_rejected()
    {
        Should.NotThrow(() => TaskLaunchService.EnsureAcceptanceMandate(Request(QualityTier.Prototype), GradesFloor(Builder(TaskProjectionKinds.Supervisor))));
        Should.NotThrow(() => TaskLaunchService.EnsureAcceptanceMandate(Request(tier: null), GradesFloor(Builder(TaskProjectionKinds.SingleAgent))));
    }

    [Theory]
    [InlineData(QualityTier.Delivery, TaskProjectionKinds.PlanMapSynth)]
    [InlineData(QualityTier.Delivery, TaskProjectionKinds.PlanMapDynamic)]
    [InlineData(QualityTier.Unattended, TaskProjectionKinds.PlanMapSynth)]
    [InlineData(QualityTier.Unattended, TaskProjectionKinds.PlanMapDynamic)]
    public void A_plan_map_launch_at_delivery_or_unattended_quality_is_not_asked_for_a_floor_it_cannot_grade(QualityTier tier, string projectionKind)
    {
        // Plan-map grades no operator floor (its items carry their own contracts), and a floor sent to it is refused
        // before the mandate runs — demanding one here would make the lane unlaunchable at these tiers.
        Should.NotThrow(() => TaskLaunchService.EnsureAcceptanceMandate(Request(tier), GradesFloor(Builder(projectionKind))));
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

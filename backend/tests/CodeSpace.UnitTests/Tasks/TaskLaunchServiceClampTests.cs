using CodeSpace.Core.Services.Tasks;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// Pins the SINGLE autonomy choke point: <see cref="TaskLaunchService.BuildAgentProfile"/> clamps the operator's
/// requested tier down to the route's <see cref="RouteCaps.AutonomyCeiling"/> and stamps the CLAMPED tier string
/// onto <see cref="ResolvedAgentProfile.AutonomyLevel"/>. That string is what flows through projection → the
/// agent.code node config → <c>AgentAutonomyPolicy.Derive</c> → the sandbox runner, so this is where a
/// Quick/Standard route's "can't run Trusted/Unleashed" guarantee is established. The clamp is pure, so it is
/// unit-pinned directly here (no DB) — the integration tier proves it propagates to the REAL permissions.
/// </summary>
[Trait("Category", "Unit")]
public class TaskLaunchServiceClampTests
{
    private static readonly TaskLaunchSeed Seed = new() { Goal = "do the thing", SurfaceKind = "chat", TeamId = Guid.NewGuid() };

    private static RoutePlan Route(string ceiling, string recommended = "") => new()
    {
        ProjectionKind = TaskProjectionKinds.SingleAgent,
        RecommendedAutonomy = recommended,
        Caps = new RouteCaps { AutonomyCeiling = ceiling },
    };

    private static TaskLaunchRequest Request(string? autonomy) => new()
    {
        TeamId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        SurfaceKind = "chat",
        Autonomy = autonomy,
    };

    [Theory]
    // The headline escalation hole: a Standard-ceiling route can never run Trusted / Unleashed however the caller asks.
    [InlineData("Unleashed", "Standard", "Standard")]
    [InlineData("Trusted", "Standard", "Standard")]
    // Requested AT or BELOW the ceiling passes through verbatim (the clamp never escalates, never over-tightens).
    [InlineData("Confined", "Standard", "Confined")]
    [InlineData("Standard", "Standard", "Standard")]
    // Case-insensitive parse (mirrors agent.code's ReadAutonomyLevel).
    [InlineData("unleashed", "standard", "Standard")]
    // No ceiling declared (blank) ⇒ the top tier ⇒ no-op ⇒ the request passes through.
    [InlineData("Trusted", "", "Trusted")]
    public void BuildAgentProfile_clamps_requested_autonomy_to_the_route_ceiling(string requested, string ceiling, string expected)
    {
        var profile = TaskLaunchService.BuildAgentProfile(Request(requested), Seed, Route(ceiling));

        profile.AutonomyLevel.ShouldBe(expected,
            customMessage: $"a '{requested}' request on a '{ceiling}'-ceiling route must stamp '{expected}' — the clamp is the single choke point feeding Derive → AgentPermissions → the sandbox runner");
    }

    [Theory]
    // A blank / null / unrecognised request folds to the route's recipe/effort default — NOT Unleashed.
    [InlineData(null, "Standard", "Standard")]
    [InlineData("", "Standard", "Standard")]
    [InlineData("   ", "Standard", "Standard")]
    [InlineData("nonsense", "Standard", "Standard")]
    // The recommended default is itself clamped to the ceiling (a recommended above-ceiling never escalates).
    [InlineData(null, "Confined", "Confined")]
    public void BuildAgentProfile_folds_a_blank_or_unknown_request_to_the_recommended_default_then_clamps(string? requested, string ceiling, string expected)
    {
        // Both RecommendedAutonomy and the ceiling are the preset's "Standard" on the production presets; a blank
        // request must NOT silently become the most-privileged tier.
        var profile = TaskLaunchService.BuildAgentProfile(Request(requested), Seed, Route(ceiling, recommended: ceiling));

        profile.AutonomyLevel.ShouldBe(expected,
            customMessage: "a null/blank/unknown autonomy request folds to the route's recommended default and is then clamped — never Unleashed");
    }

    [Fact]
    public void BuildAgentProfile_with_no_recommended_and_no_request_defaults_to_Standard_the_safe_floor()
    {
        // Neither the request nor the recommended default names a tier, and there's no ceiling → the parse falls
        // to the safe Standard default (the historical permission default), never Unleashed.
        var profile = TaskLaunchService.BuildAgentProfile(Request(null), Seed, Route(ceiling: "", recommended: ""));

        profile.AutonomyLevel.ShouldBe("Standard");
    }
}

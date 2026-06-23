using CodeSpace.Core.Services.Tasks;
using CodeSpace.Messages.Agents;
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

    // ── Multi-repo launch: the related-repos → profile projection (the OTHER pure choke point on BuildAgentProfile) ──

    private static readonly Guid PrimaryRepo = Guid.NewGuid();
    private static readonly Guid RelatedRepo = Guid.NewGuid();

    private static TaskLaunchRequest MultiRepoRequest(Guid? primary, params TaskRelatedRepository[] related) => new()
    {
        TeamId = Guid.NewGuid(),
        ActorUserId = Guid.NewGuid(),
        SurfaceKind = "chat",
        RepositoryId = primary,
        RelatedRepositories = related.Length > 0 ? related : null,
    };

    [Fact]
    public void BuildAgentProfile_with_no_related_repos_leaves_RelatedRepositories_null_byte_identical()
    {
        var profile = TaskLaunchService.BuildAgentProfile(MultiRepoRequest(PrimaryRepo), Seed, Route("Standard"));

        profile.RelatedRepositories.ShouldBeNull("no related repos ⇒ unset ⇒ the projection omits the key ⇒ a single-repo run is byte-identical");
    }

    [Fact]
    public void BuildAgentProfile_projects_each_related_repo_onto_the_profile_through_the_shared_authoring()
    {
        var profile = TaskLaunchService.BuildAgentProfile(
            MultiRepoRequest(PrimaryRepo,
                new TaskRelatedRepository { RepositoryId = RelatedRepo, Alias = "  api  ", Access = "write" }),
            Seed, Route("Standard"));

        profile.RelatedRepositories.ShouldNotBeNull();
        var related = profile.RelatedRepositories!.ShouldHaveSingleItem();
        related.RepositoryId.ShouldBe(RelatedRepo);
        related.Alias.ShouldBe("api", "the alias is trimmed by the shared authoring path");
        related.Access.ShouldBe(WorkspaceAccess.Write, "an authored 'write' access flows onto the spec");
    }

    [Theory]
    [InlineData("write", WorkspaceAccess.Write)]
    [InlineData("WRITE", WorkspaceAccess.Write)]   // case-insensitive (shared with the JSON parse)
    [InlineData("read", WorkspaceAccess.Read)]
    [InlineData(null, WorkspaceAccess.Read)]       // absent ⇒ read-only context (the safe default)
    [InlineData("garbage", WorkspaceAccess.Read)]
    public void BuildAgentProfile_defaults_related_access_to_read_unless_write(string? access, WorkspaceAccess expected)
    {
        var profile = TaskLaunchService.BuildAgentProfile(
            MultiRepoRequest(PrimaryRepo, new TaskRelatedRepository { RepositoryId = RelatedRepo, Access = access }),
            Seed, Route("Standard"));

        profile.RelatedRepositories!.Single().Access.ShouldBe(expected);
    }

    [Fact]
    public void BuildAgentProfile_keeps_related_repos_in_authored_order()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();

        var profile = TaskLaunchService.BuildAgentProfile(
            MultiRepoRequest(PrimaryRepo,
                new TaskRelatedRepository { RepositoryId = a, Alias = "api" },
                new TaskRelatedRepository { RepositoryId = b, Alias = "web" }),
            Seed, Route("Standard"));

        profile.RelatedRepositories!.Select(r => r.RepositoryId).ShouldBe(new[] { a, b });
    }

    [Fact]
    public void BuildAgentProfile_with_related_but_no_primary_repo_throws_fail_loud()
    {
        // A related repo has nowhere to anchor without a primary — fail LOUD at launch (mirroring the agent.code node),
        // not silently drop the authored multi-repo intent into a no-repo run.
        var ex = Should.Throw<ArgumentException>(() =>
            TaskLaunchService.BuildAgentProfile(
                MultiRepoRequest(primary: null, new TaskRelatedRepository { RepositoryId = RelatedRepo }),
                Seed, Route("Standard")));

        ex.Message.ShouldContain("require a primary repository");
    }

    [Fact]
    public void BuildAgentProfile_with_a_primary_but_no_related_does_not_throw()
    {
        // Guard fires ONLY on related-without-primary; a single-repo (or analysis-only) launch is unaffected.
        Should.NotThrow(() => TaskLaunchService.BuildAgentProfile(MultiRepoRequest(PrimaryRepo), Seed, Route("Standard")));
    }
}

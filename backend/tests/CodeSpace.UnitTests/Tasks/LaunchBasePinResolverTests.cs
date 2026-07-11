using CodeSpace.Core.Services.Tasks.Launch;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Tasks;
using Shouldly;

namespace CodeSpace.UnitTests.Tasks;

/// <summary>
/// <see cref="LaunchBasePinResolver.CollectScope"/> — the pure eligibility policy of S1's launch base vector:
/// WHICH repos get pinned and at WHICH hard ref. The DB + transport legs are covered by the launch integration flow.
/// </summary>
[Trait("Category", "Unit")]
public sealed class LaunchBasePinResolverTests
{
    private static readonly Guid Primary = Guid.NewGuid();
    private static readonly Guid Api = Guid.NewGuid();

    private static readonly IReadOnlyDictionary<Guid, string> NoSessionRefs = new Dictionary<Guid, string>();

    private static TaskLaunchSeed Seed(string? baseBranch = null) =>
        new() { Goal = "g", SurfaceKind = "chat", TeamId = Guid.NewGuid(), BaseBranch = baseBranch };

    [Fact]
    public void The_primary_pins_at_the_operators_BaseBranch_and_related_repos_at_their_authored_refs()
    {
        var profile = new ResolvedAgentProfile
        {
            RepositoryId = Primary,
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Api, Access = WorkspaceAccess.Write, Ref = "release/2.x" } },
        };

        var scope = LaunchBasePinResolver.CollectScope(Seed(baseBranch: "main"), profile, NoSessionRefs);

        scope[Primary].ShouldBe("main", "the primary's hard ref is the operator's launch pin");
        scope[Api].ShouldBe("release/2.x", "a related repo's hard ref is its authored ref");
    }

    [Fact]
    public void A_blank_BaseBranch_and_a_refless_related_repo_pin_at_the_default_branch()
    {
        var profile = new ResolvedAgentProfile
        {
            RepositoryId = Primary,
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Api, Access = WorkspaceAccess.Read } },
        };

        var scope = LaunchBasePinResolver.CollectScope(Seed(baseBranch: "  "), profile, NoSessionRefs);

        scope[Primary].ShouldBeNull("null ref = the default branch's tip (still pinned — the vector's whole point is a fresh launch's immutable base)");
        scope[Api].ShouldBeNull();
    }

    [Fact]
    public void A_repo_riding_a_session_soft_ref_is_excluded_from_the_vector()
    {
        // A soft ref's contract is "the prior branch, or the default if pruned" — a disjunction one commit cannot
        // express, so the continuing repo stays unpinned (legacy) rather than turning the soft fallback into a
        // hard checkout failure after a squash-merge prunes the branch.
        var profile = new ResolvedAgentProfile
        {
            RepositoryId = Primary,
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "api", RepositoryId = Api, Access = WorkspaceAccess.Write } },
        };

        var scope = LaunchBasePinResolver.CollectScope(Seed(), profile, new Dictionary<Guid, string> { [Primary] = "run-1/primary" });

        scope.ContainsKey(Primary).ShouldBeFalse("the primary continues on a session branch — unpinned by design");
        scope.ContainsKey(Api).ShouldBeTrue("the related repo has no session ref — it still pins");
    }

    [Fact]
    public void A_related_repo_duplicating_the_primary_is_collected_once()
    {
        var profile = new ResolvedAgentProfile
        {
            RepositoryId = Primary,
            RelatedRepositories = new[] { new WorkspaceRepositorySpec { Alias = "self", RepositoryId = Primary, Access = WorkspaceAccess.Write, Ref = "other" } },
        };

        var scope = LaunchBasePinResolver.CollectScope(Seed(baseBranch: "main"), profile, NoSessionRefs);

        scope.Count.ShouldBe(1);
        scope[Primary].ShouldBe("main", "the primary's own ref wins — the workspace projection drops the duplicate related entry too");
    }

    [Fact]
    public void No_repos_yields_an_empty_scope()
    {
        LaunchBasePinResolver.CollectScope(Seed(), new ResolvedAgentProfile(), NoSessionRefs).ShouldBeEmpty("an analysis-only launch has nothing to pin");
    }
}

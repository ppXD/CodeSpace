using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// The MEMBERSHIP axis of the act-as-user pre-flight. Providers map their native access levels onto the
/// neutral <see cref="RepositoryRole"/> ladder; the gate compares the actor's role against the per-capability
/// requirement the module declares (default Read). These tests pin the mappings, the declared requirements,
/// the gate's default, AND — crucially — that the role compare reproduces the OLD hardcoded thresholds it
/// replaces (GitLab review ≥ Developer; GitHub review = repo accessible). Nothing behavioural changes.
/// </summary>
[Trait("Category", "Unit")]
public class RepositoryRoleMappingTests
{
    // ── GitLab access_level → RepositoryRole ─────────────────────────────────────────

    [Theory]
    [InlineData(null, RepositoryRole.None)]    // visible project, no membership grant
    [InlineData(0, RepositoryRole.None)]
    [InlineData(5, RepositoryRole.None)]        // below Guest
    [InlineData(10, RepositoryRole.Read)]       // Guest
    [InlineData(20, RepositoryRole.Triage)]     // Reporter
    [InlineData(29, RepositoryRole.Triage)]     // just below Developer rounds DOWN
    [InlineData(30, RepositoryRole.Write)]      // Developer
    [InlineData(40, RepositoryRole.Maintain)]   // Maintainer
    [InlineData(50, RepositoryRole.Admin)]      // Owner
    [InlineData(60, RepositoryRole.Admin)]      // above Owner (future) clamps to Admin
    public void GitLab_maps_access_level_to_role(int? level, RepositoryRole expected) =>
        GitLabRepositoryProvider.MapAccessLevel(level).ShouldBe(expected);

    // ── GitHub permission flags → RepositoryRole (highest granted wins) ──────────────

    [Theory]
    [InlineData(false, false, false, false, false, RepositoryRole.None)]      // no access
    [InlineData(false, false, false, false, true, RepositoryRole.Read)]       // pull
    [InlineData(false, false, false, true, true, RepositoryRole.Triage)]      // triage
    [InlineData(false, false, true, true, true, RepositoryRole.Write)]        // push
    [InlineData(false, true, true, true, true, RepositoryRole.Maintain)]      // maintain
    [InlineData(true, true, true, true, true, RepositoryRole.Admin)]          // admin wins over all
    [InlineData(true, false, false, false, false, RepositoryRole.Admin)]      // admin alone
    public void GitHub_maps_permission_flags_to_role(bool admin, bool maintain, bool push, bool triage, bool pull, RepositoryRole expected) =>
        GitHubRepositoryProvider.MapPermissions(admin, maintain, push, triage, pull).ShouldBe(expected);

    // ── Per-capability requirement: module data + the gate's Read default ────────────

    [Fact]
    public void GitLab_requires_Write_for_a_review() =>
        ActorIdentityRequirementGate.RequiredRoleFor(new GitLabProviderModule(), typeof(IPullRequestReviewCapability))
            .ShouldBe(RepositoryRole.Write, "preserves GitLab's historical ≥Developer membership threshold for MR review/notes");

    [Fact]
    public void GitHub_requires_only_Read_for_a_review() =>
        ActorIdentityRequirementGate.RequiredRoleFor(new GitHubProviderModule(), typeof(IPullRequestReviewCapability))
            .ShouldBe(RepositoryRole.Read, "preserves GitHub's historical 'repo is accessible' membership threshold for reviews");

    [Fact]
    public void A_capability_with_no_declared_role_defaults_to_the_Read_floor() =>
        ActorIdentityRequirementGate.RequiredRoleFor(new GitLabProviderModule(), typeof(IRepositoryCatalogCapability))
            .ShouldBe(RepositoryRole.Read, "an undeclared capability ⇒ the Read floor — the actor must at least see the repo");

    [Fact]
    public void A_null_module_defaults_to_the_Read_floor() =>
        ActorIdentityRequirementGate.RequiredRoleFor(null, typeof(IPullRequestReviewCapability)).ShouldBe(RepositoryRole.Read);

    [Fact]
    public void A_null_capability_defaults_to_the_Read_floor() =>
        ActorIdentityRequirementGate.RequiredRoleFor(new GitLabProviderModule(), null).ShouldBe(RepositoryRole.Read);

    // ── Behavior preservation: actorRole >= required reproduces the OLD thresholds ───

    [Theory]
    [InlineData(10, false)]   // Guest    → below Developer → denied (was denied)
    [InlineData(20, false)]   // Reporter → below Developer → denied (was denied)
    [InlineData(29, false)]   // just below Developer → denied
    [InlineData(30, true)]    // Developer → allowed (was allowed)
    [InlineData(40, true)]    // Maintainer → allowed
    [InlineData(50, true)]    // Owner → allowed
    public void GitLab_review_allow_decision_matches_the_old_developer_threshold(int level, bool expectedAllowed)
    {
        var module = new GitLabProviderModule();
        var actorRole = GitLabRepositoryProvider.MapAccessLevel(level);
        var required = ActorIdentityRequirementGate.RequiredRoleFor(module, typeof(IPullRequestReviewCapability));

        (actorRole >= required).ShouldBe(expectedAllowed);
    }

    [Theory]
    [InlineData(false, false, false, false, false, false)]   // no access → denied (was 403)
    [InlineData(false, false, false, false, true, true)]     // pull/read → allowed (was "accessible")
    [InlineData(false, false, true, false, true, true)]      // push → allowed
    [InlineData(true, false, false, false, false, true)]     // admin → allowed
    public void GitHub_review_allow_decision_matches_the_old_accessible_threshold(bool admin, bool maintain, bool push, bool triage, bool pull, bool expectedAllowed)
    {
        var module = new GitHubProviderModule();
        var actorRole = GitHubRepositoryProvider.MapPermissions(admin, maintain, push, triage, pull);
        var required = ActorIdentityRequirementGate.RequiredRoleFor(module, typeof(IPullRequestReviewCapability));

        (actorRole >= required).ShouldBe(expectedAllowed);
    }
}

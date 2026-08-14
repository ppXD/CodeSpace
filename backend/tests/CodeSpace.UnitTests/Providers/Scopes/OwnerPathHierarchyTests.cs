using CodeSpace.Core.Services.Webhooks.Registration;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Scopes;

/// <summary>
/// The rule that decides whether a hook already covers an owner. It is pure, and it is the whole of
/// the answer to "does binding under a subgroup need a second hook" — which is worth pinning without
/// a database, because getting it wrong does not fail loudly: it produces two hooks that both work,
/// and every push under the subgroup starts two workflow runs.
/// </summary>
[Trait("Category", "Unit")]
public class OwnerPathHierarchyTests
{
    [Fact]
    public void A_nested_group_lists_itself_then_every_group_above_it()
    {
        // Nearest first, because the caller takes the first match — the tightest hook covering the
        // owner, not whichever row the database happened to hand back.
        OwnerPathHierarchy.SelfAndAncestors("acme/platform/web").ShouldBe(new[] { "acme/platform/web", "acme/platform", "acme" });
    }

    [Fact]
    public void A_single_segment_owner_is_its_own_only_ancestor()
    {
        // GitHub's organization login has no separator, so the nesting question never arises there
        // and one rule serves both providers.
        OwnerPathHierarchy.SelfAndAncestors("acme").ShouldBe(new[] { "acme" });
    }

    [Theory]
    [InlineData("acme", "acme/platform/web", true)]          // a group hook fires for its subgroups' projects
    [InlineData("acme/platform", "acme/platform/web", true)]
    [InlineData("acme/platform/web", "acme/platform/web", true)]
    [InlineData("acme/platform/web", "acme/platform", false)]  // a descendant covers nothing above it
    [InlineData("other", "acme/platform", false)]
    public void Covers_answers_whether_an_ancestors_hook_reaches_a_descendant(string ancestorPath, string ownerPath, bool covers)
    {
        OwnerPathHierarchy.Covers(ancestorPath, ownerPath).ShouldBe(covers);
    }

    [Fact]
    public void A_string_prefix_that_is_not_a_path_prefix_covers_nothing()
    {
        // The one that a naive StartsWith gets wrong. `acme/plat` is a prefix of `acme/platform` and
        // is a different group — matching it would reuse a hook registered somewhere else entirely,
        // and the repository would be covered by nothing while the page claimed otherwise.
        OwnerPathHierarchy.Covers("acme/plat", "acme/platform/web").ShouldBeFalse();
    }
}

using CodeSpace.Core.Services.Providers.Scopes;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Scopes;

[Trait("Category", "Unit")]
public class ScopeRequirementTests
{
    [Fact]
    public void None_is_always_satisfied_even_with_empty_grants()
    {
        ScopeRequirement.None.IsSatisfied(Array.Empty<string>()).ShouldBeTrue();
        ScopeRequirement.None.IsSatisfied(null).ShouldBeTrue();
    }

    [Fact]
    public void Of_single_scope_satisfied_only_when_granted()
    {
        var req = ScopeRequirement.Of("api");

        req.IsSatisfied(new[] { "api" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read_api" }).ShouldBeFalse();
        req.IsSatisfied(Array.Empty<string>()).ShouldBeFalse();
    }

    [Fact]
    public void AnyOf_satisfied_when_any_alternative_matches()
    {
        var req = ScopeRequirement.AnyOf("repo", "public_repo");

        req.IsSatisfied(new[] { "repo" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "public_repo" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "repo", "read:user" }).ShouldBeTrue();   // extra scopes don't hurt
        req.IsSatisfied(new[] { "read:user" }).ShouldBeFalse();          // neither alternative satisfied
    }

    [Fact]
    public void Compound_alternative_requires_all_scopes_in_set()
    {
        // "[[api]] OR [[read_api, read_repository]]" — second alternative needs BOTH scopes.
        var req = new ScopeRequirement(new IReadOnlyList<string>[]
        {
            new[] { "api" },
            new[] { "read_api", "read_repository" }
        });

        req.IsSatisfied(new[] { "api" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read_api", "read_repository" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "read_api" }).ShouldBeFalse();
        req.IsSatisfied(new[] { "read_repository" }).ShouldBeFalse();
    }

    [Fact]
    public void IsSatisfied_is_case_insensitive()
    {
        var req = ScopeRequirement.Of("API");

        req.IsSatisfied(new[] { "api" }).ShouldBeTrue();
        req.IsSatisfied(new[] { "Api" }).ShouldBeTrue();
    }

    [Fact]
    public void MissingScopesForBestAlternative_returns_null_when_satisfied()
    {
        var req = ScopeRequirement.AnyOf("repo", "public_repo");

        req.MissingScopesForBestAlternative(new[] { "repo" }).ShouldBeNull();
    }

    [Fact]
    public void MissingScopesForBestAlternative_returns_shortest_diff()
    {
        // User has nothing. Two alternatives: [api] (1 missing) or [read_api, read_repository] (2 missing).
        // Pick the alternative with fewest missing → "api".
        var req = new ScopeRequirement(new IReadOnlyList<string>[]
        {
            new[] { "api" },
            new[] { "read_api", "read_repository" }
        });

        var missing = req.MissingScopesForBestAlternative(Array.Empty<string>());

        missing.ShouldNotBeNull();
        missing.ShouldBe(new[] { "api" });
    }

    [Fact]
    public void MissingScopesForBestAlternative_prefers_partially_satisfied_alternative()
    {
        // User has read_api only. Alternatives:
        //   [api] (1 missing)
        //   [read_api, read_repository] (only read_repository missing — 1 missing)
        // Tie on count, prefer shorter alternative ([api]).
        var req = new ScopeRequirement(new IReadOnlyList<string>[]
        {
            new[] { "api" },
            new[] { "read_api", "read_repository" }
        });

        var missing = req.MissingScopesForBestAlternative(new[] { "read_api" });

        missing.ShouldNotBeNull();
        missing.Count.ShouldBe(1);
    }
}

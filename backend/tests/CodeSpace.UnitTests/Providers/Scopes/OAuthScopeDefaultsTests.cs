using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Scopes;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Scopes;

/// <summary>
/// Pins OAuthScopeDefaults.Compute (derives the OAuth request scopes from the per-capability requirement
/// map) AND the two real modules' resolved defaults. The module pins are the NON-BREAKING guard: the
/// derived set must equal the historical hand-declared one (GitLab [api], GitHub [repo, read:user]).
/// </summary>
[Trait("Category", "Unit")]
public class OAuthScopeDefaultsTests
{
    // ── Pure helper ─────────────────────────────────────────────────────────────

    [Fact]
    public void Unions_the_preferred_first_alternative_of_each_capability()
    {
        var reqs = new Dictionary<Type, ScopeRequirement>
        {
            [typeof(string)] = ScopeRequirement.AnyOf("repo", "public_repo"),   // broadest first → repo
            [typeof(int)] = ScopeRequirement.AnyOf("repo", "admin:repo_hook"),  // repo again → de-duped
        };

        OAuthScopeDefaults.Compute(System.Array.Empty<string>(), reqs).ShouldBe(new[] { "repo" });
    }

    [Fact]
    public void Appends_extras_after_capability_scopes_deduped()
    {
        var reqs = new Dictionary<Type, ScopeRequirement> { [typeof(string)] = ScopeRequirement.Of("repo") };

        // repo from the capability; read:user from extras; the duplicate repo in extras is dropped.
        OAuthScopeDefaults.Compute(new[] { "read:user", "repo" }, reqs).ShouldBe(new[] { "repo", "read:user" });
    }

    [Fact]
    public void A_None_requirement_contributes_no_scope()
    {
        var reqs = new Dictionary<Type, ScopeRequirement> { [typeof(string)] = ScopeRequirement.None };

        OAuthScopeDefaults.Compute(System.Array.Empty<string>(), reqs).ShouldBeEmpty();
    }

    [Fact]
    public void Dedup_is_case_insensitive_first_seen_wins()
    {
        var reqs = new Dictionary<Type, ScopeRequirement> { [typeof(string)] = ScopeRequirement.Of("API") };

        OAuthScopeDefaults.Compute(new[] { "api" }, reqs).ShouldBe(new[] { "API" });
    }

    [Fact]
    public void A_new_capability_extends_the_request_automatically_no_drift()
    {
        var before = new Dictionary<Type, ScopeRequirement> { [typeof(string)] = ScopeRequirement.Of("api") };
        var after = new Dictionary<Type, ScopeRequirement>
        {
            [typeof(string)] = ScopeRequirement.Of("api"),
            [typeof(int)] = ScopeRequirement.Of("write_repository"),
        };

        OAuthScopeDefaults.Compute(System.Array.Empty<string>(), before).ShouldBe(new[] { "api" });
        OAuthScopeDefaults.Compute(System.Array.Empty<string>(), after)
            .ShouldBe(new[] { "api", "write_repository" }, "a new capability's scope joins the OAuth request automatically — no second place to edit");
    }

    // ── Non-breaking pins: derived module default == the historical hand-declared set ─────────────

    [Fact]
    public void GitLab_default_still_resolves_to_api() =>
        new GitLabProviderModule().DefaultOAuthScopes.ShouldBe(new[] { "api" });

    [Fact]
    public void GitHub_default_still_resolves_to_repo_then_read_user() =>
        new GitHubProviderModule().DefaultOAuthScopes.ShouldBe(new[] { "repo", "read:user" });
}

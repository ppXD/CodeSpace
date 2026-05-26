using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Modules;
using CodeSpace.Core.Services.Providers.Scopes;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Scopes;

public class ScopeCheckerTests
{
    [Fact]
    public void Check_returns_satisfied_when_granted_scopes_match_requirement()
    {
        var checker = BuildChecker();

        var outcome = checker.Check(ProviderKind.GitHub, typeof(IRepositoryCatalogCapability), new[] { "repo" });

        outcome.IsSatisfied.ShouldBeTrue();
        outcome.MissingScopes.ShouldBeEmpty();
    }

    [Fact]
    public void Check_returns_missing_scopes_when_requirement_unsatisfied()
    {
        var checker = BuildChecker();

        var outcome = checker.Check(ProviderKind.GitLab, typeof(IWebhookRegistrationCapability), new[] { "read_api" });

        outcome.IsSatisfied.ShouldBeFalse();
        outcome.MissingScopes.ShouldContain("api");
    }

    [Fact]
    public void Check_returns_satisfied_for_unknown_provider()
    {
        // ProviderKind.Git has no module — must NOT block, otherwise future providers brick
        // before their module is declared.
        var checker = BuildChecker();

        var outcome = checker.Check(ProviderKind.Git, typeof(IRepositoryCatalogCapability), Array.Empty<string>());

        outcome.IsSatisfied.ShouldBeTrue();
    }

    [Fact]
    public void EnsureCapability_skips_non_OAuth_credentials()
    {
        // PAT credentials have no Scopes column; user-side token decides. Pre-flight would
        // wrongly reject if we treated them like OAuth.
        var checker = BuildChecker();
        var pat = new Credential { AuthType = AuthType.Pat, Scopes = null };

        Should.NotThrow(() => checker.EnsureCapability(pat, ProviderKind.GitLab, typeof(IWebhookRegistrationCapability)));
    }

    [Fact]
    public void EnsureCapability_passes_when_OAuth_scopes_satisfy_requirement()
    {
        var checker = BuildChecker();
        var oauth = new Credential { AuthType = AuthType.OAuth, Scopes = new List<string> { "api" } };

        Should.NotThrow(() => checker.EnsureCapability(oauth, ProviderKind.GitLab, typeof(IWebhookRegistrationCapability)));
    }

    [Fact]
    public void EnsureCapability_throws_typed_exception_when_OAuth_scope_missing()
    {
        var checker = BuildChecker();
        var oauth = new Credential { AuthType = AuthType.OAuth, Scopes = new List<string> { "read_api" } };

        var ex = Should.Throw<ProviderInsufficientScopeException>(
            () => checker.EnsureCapability(oauth, ProviderKind.GitLab, typeof(IWebhookRegistrationCapability)));

        ex.ProviderKind.ShouldBe(ProviderKind.GitLab);
        ex.CapabilityName.ShouldBe(nameof(IWebhookRegistrationCapability));
        ex.MissingScopes.ShouldContain("api");
    }

    private static ScopeChecker BuildChecker()
    {
        var catalog = new ProviderModuleCatalog(new IProviderModule[] { new GitHubProviderModule(), new GitLabProviderModule() });
        return new ScopeChecker(catalog);
    }
}

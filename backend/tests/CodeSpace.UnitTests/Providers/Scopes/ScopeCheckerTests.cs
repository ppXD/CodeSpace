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

[Trait("Category", "Unit")]
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

    // ── EnsureCapability: gates on scope KNOWLEDGE, not auth TYPE ──────────────────────────────
    // Every case below proves the auth type is irrelevant — only whether scopes are known + sufficient
    // decides. (Pre-PR② this gate skipped every non-OAuth credential by type; now a PAT with captured
    // scopes is pre-checked just like OAuth, so a read-only PAT is caught here, not mid-run at the wire.)

    [Theory]
    [InlineData(AuthType.OAuth)]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    [InlineData(AuthType.GroupAccessToken)]
    [InlineData(AuthType.GitHubApp)]
    public void EnsureCapability_skips_when_scopes_are_unknown_for_every_auth_type(AuthType authType)
    {
        // null Scopes = "never captured" (a token type that can't expose them, or one linked before
        // scope capture existed). Unknown is NOT "zero scopes" — skip the pre-check and let any wire
        // 403 flow through the per-provider error mapper. Identical rule for OAuth and every token type.
        var credential = new Credential { AuthType = authType, Scopes = null };

        Should.NotThrow(() => BuildChecker().EnsureCapability(credential, ProviderKind.GitLab, typeof(IWebhookRegistrationCapability)));
    }

    [Theory]
    [InlineData(AuthType.OAuth)]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    public void EnsureCapability_passes_when_known_scopes_satisfy_for_every_auth_type(AuthType authType)
    {
        // A credential whose CAPTURED scopes are sufficient passes pre-flight regardless of auth type —
        // the gate reads scope DATA, never the auth TYPE. (`api` covers GitLab webhook registration.)
        var credential = new Credential { AuthType = authType, Scopes = new List<string> { "api" } };

        Should.NotThrow(() => BuildChecker().EnsureCapability(credential, ProviderKind.GitLab, typeof(IWebhookRegistrationCapability)));
    }

    [Theory]
    [InlineData(AuthType.OAuth)]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.ProjectAccessToken)]
    public void EnsureCapability_throws_when_known_scopes_miss_for_every_auth_type(AuthType authType)
    {
        // The user's actual bug: a read-only token (`read_api` only) is now caught HERE at pre-flight,
        // not mid-run at the wire. read_api cannot register a webhook (needs `api`) — for any auth type.
        var credential = new Credential { AuthType = authType, Scopes = new List<string> { "read_api" } };

        var ex = Should.Throw<ProviderInsufficientScopeException>(
            () => BuildChecker().EnsureCapability(credential, ProviderKind.GitLab, typeof(IWebhookRegistrationCapability)));

        ex.ProviderKind.ShouldBe(ProviderKind.GitLab);
        ex.CapabilityName.ShouldBe(nameof(IWebhookRegistrationCapability));
        ex.MissingScopes.ShouldContain("api");
    }

    [Fact]
    public void EnsureCapability_checks_a_known_empty_scope_set_and_throws_when_capability_needs_one()
    {
        // Mirrors CredentialService's warning semantics exactly: null = unknown (skipped above), but a
        // NON-null list — including an explicitly empty one — is KNOWN and gets checked. Empty can't
        // satisfy a scope-requiring capability, so it throws. (In practice both probes emit null, never
        // empty — this pins the boundary so the gate and the surfaced warning never diverge.)
        var credential = new Credential { AuthType = AuthType.Pat, Scopes = new List<string>() };

        Should.Throw<ProviderInsufficientScopeException>(
            () => BuildChecker().EnsureCapability(credential, ProviderKind.GitLab, typeof(IWebhookRegistrationCapability)));
    }

    [Fact]
    public void EnsureCapability_passes_a_known_empty_scope_set_when_the_capability_needs_no_scope()
    {
        // Empty (known) scopes are CHECKED, not blanket-rejected: against a None requirement (GitHub
        // probe needs no scope) the check is satisfied → no throw. Proves empty is evaluated against
        // the actual per-capability requirement, never assumed insufficient.
        var credential = new Credential { AuthType = AuthType.Pat, Scopes = new List<string>() };

        Should.NotThrow(() => BuildChecker().EnsureCapability(credential, ProviderKind.GitHub, typeof(ICredentialProbeCapability)));
    }

    private static ScopeChecker BuildChecker()
    {
        var catalog = new ProviderModuleCatalog(new IProviderModule[] { new GitHubProviderModule(), new GitLabProviderModule() });
        return new ScopeChecker(catalog);
    }
}

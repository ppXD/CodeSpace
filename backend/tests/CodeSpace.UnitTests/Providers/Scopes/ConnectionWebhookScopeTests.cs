using CodeSpace.Core.Services.Providers.Capabilities;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Scopes;

/// <summary>
/// The two halves of one decision about GitHub organization hooks: the grant is declared so the bind
/// pre-flight can name it, and it is deliberately NOT asked for at consent time.
///
/// <para>They are pinned together because either one alone reads like a mistake. A declared
/// requirement that also joined <c>DefaultOAuthScopes</c> — which is what happens by default, since
/// the defaults are derived from the requirement map — would put "Full control of organization
/// webhooks" in front of every operator connecting GitHub, most of whom will never leave
/// per-repository scope. Dropping the requirement instead would spare the consent screen and lose
/// the pre-flight, leaving a switch to connection-wide scope to fail later as a bare 403.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ConnectionWebhookScopeTests
{
    [Fact]
    public void GitHub_organization_hooks_require_their_own_grant()
    {
        var requirement = new GitHubProviderModule().CapabilityScopeRequirements[typeof(IConnectionWebhookRegistrationCapability)];

        requirement.IsSatisfied(new[] { "admin:org_hook" }).ShouldBeTrue();

        // The one that matters: `repo` is a superset of admin:repo_hook and NOT of admin:org_hook.
        // Accepting it here would pass a credential that cannot create the hook, turning a legible
        // pre-flight refusal into a dead-lettered registration hours later.
        requirement.IsSatisfied(new[] { "repo" }).ShouldBeFalse();
        requirement.IsSatisfied(new[] { "admin:repo_hook" }).ShouldBeFalse();
    }

    [Fact]
    public void Declaring_that_grant_does_not_put_it_on_everyones_consent_screen()
    {
        new GitHubProviderModule().DefaultOAuthScopes.ShouldNotContain("admin:org_hook",
            customMessage: "Connection-wide scope is opt-in and off by default; its grant must be too. Asking every GitHub connection for organization-webhook administration to support a mode almost none of them use is the consent inflation this module is written to avoid.");
    }

    [Fact]
    public void GitLab_group_hooks_need_no_scope_beyond_the_one_project_hooks_already_need()
    {
        // GitLab gates group hooks by PLAN and by group membership, not by a scope a token could be
        // re-issued with — `api` already covers the endpoint. So there is nothing to ask for, and
        // nothing a pre-flight could check: only the call settles whether the instance will answer.
        var requirement = new GitLabProviderModule().CapabilityScopeRequirements[typeof(IConnectionWebhookRegistrationCapability)];

        requirement.IsSatisfied(new[] { "api" }).ShouldBeTrue();
        requirement.IsSatisfied(new[] { "read_api" }).ShouldBeFalse();

        new GitLabProviderModule().DefaultOAuthScopes.ShouldBe(new[] { "api" },
            customMessage: "GitLab's consent must be unchanged by connection-wide scope — `api` already covered it.");
    }
}

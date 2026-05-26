using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// Authorize URL builder is the part of the OAuth client that has no network call — pure
/// URL composition. These tests pin the parameter contract that GitHub / GitLab expect.
/// Any drift here breaks the entire OAuth handshake silently (provider just redirects to its
/// error page), so the assertions deliberately check exact param presence and values.
/// </summary>
public class OAuthClientAuthorizeUrlTests
{
    private static readonly Uri Redirect = new("https://app.codespace.dev/api/credentials/oauth/callback");

    [Fact]
    public void GitHub_authorize_url_contains_required_params()
    {
        var client = new GitHubOAuthClient(httpClientFactory: null!, TimeProvider.System);

        var url = client.BuildAuthorizeUrl(new OAuthAuthorizeInput
        {
            Instance = BuildInstance("https://github.com", ProviderKind.GitHub),
            ClientId = "Iv23liExampleClient",
            State = "STATE-abc",
            CodeChallengeS256 = "CHAL-xyz",
            RedirectUri = Redirect,
            Scopes = new[] { "repo", "read:user" }
        });

        url.AbsoluteUri.ShouldStartWith("https://github.com/login/oauth/authorize?");
        url.Query.ShouldContain("client_id=Iv23liExampleClient");
        url.Query.ShouldContain("state=STATE-abc");
        url.Query.ShouldContain("code_challenge=CHAL-xyz");
        url.Query.ShouldContain("code_challenge_method=S256");
        // GitHub uses space-separated scopes (URL-encoded as %20).
        url.Query.ShouldContain("scope=repo%20read%3Auser");
        url.Query.ShouldContain($"redirect_uri={Uri.EscapeDataString(Redirect.ToString())}");
    }

    [Fact]
    public void GitHub_authorize_url_includes_prompt_consent_to_force_reauthorization()
    {
        // Pinning prompt=consent — without it, GitHub silently re-issues a token when the
        // user is still signed in and the OAuth app is in their authorized apps list, which
        // robs the operator of the chance to change which organizations are granted access
        // on reconnect. Documented for GitHub Apps; observed to work for OAuth Apps too.
        var client = new GitHubOAuthClient(httpClientFactory: null!, TimeProvider.System);

        var url = client.BuildAuthorizeUrl(new OAuthAuthorizeInput
        {
            Instance = BuildInstance("https://github.com", ProviderKind.GitHub),
            ClientId = "id", State = "s", CodeChallengeS256 = "c", RedirectUri = Redirect
        });

        url.Query.ShouldContain("prompt=consent");
    }

    [Fact]
    public void GitHub_authorize_url_omits_scope_when_none_requested()
    {
        var client = new GitHubOAuthClient(httpClientFactory: null!, TimeProvider.System);

        var url = client.BuildAuthorizeUrl(new OAuthAuthorizeInput
        {
            Instance = BuildInstance("https://github.com", ProviderKind.GitHub),
            ClientId = "id", State = "s", CodeChallengeS256 = "c", RedirectUri = Redirect
        });

        url.Query.ShouldNotContain("scope=");
    }

    [Fact]
    public void GitHub_authorize_url_respects_enterprise_base_url()
    {
        var client = new GitHubOAuthClient(httpClientFactory: null!, TimeProvider.System);

        var url = client.BuildAuthorizeUrl(new OAuthAuthorizeInput
        {
            Instance = BuildInstance("https://github.northwind.dev/", ProviderKind.GitHub),
            ClientId = "id", State = "s", CodeChallengeS256 = "c", RedirectUri = Redirect
        });

        // Trailing slash on BaseUrl is dropped; path appended verbatim.
        url.AbsoluteUri.ShouldStartWith("https://github.northwind.dev/login/oauth/authorize?");
    }

    [Fact]
    public void GitLab_authorize_url_contains_required_params()
    {
        var client = new GitLabOAuthClient(httpClientFactory: null!, TimeProvider.System);

        var url = client.BuildAuthorizeUrl(new OAuthAuthorizeInput
        {
            Instance = BuildInstance("https://gitlab.com", ProviderKind.GitLab),
            ClientId = "client123",
            State = "STATE-abc",
            CodeChallengeS256 = "CHAL-xyz",
            RedirectUri = Redirect,
            Scopes = new[] { "api", "read_user" }
        });

        url.AbsoluteUri.ShouldStartWith("https://gitlab.com/oauth/authorize?");
        url.Query.ShouldContain("client_id=client123");
        url.Query.ShouldContain("response_type=code");
        url.Query.ShouldContain("state=STATE-abc");
        url.Query.ShouldContain("code_challenge=CHAL-xyz");
        url.Query.ShouldContain("code_challenge_method=S256");
        url.Query.ShouldContain("scope=api%20read_user");
    }

    [Fact]
    public void GitLab_authorize_url_respects_self_hosted_base_url()
    {
        var client = new GitLabOAuthClient(httpClientFactory: null!, TimeProvider.System);

        var url = client.BuildAuthorizeUrl(new OAuthAuthorizeInput
        {
            Instance = BuildInstance("https://gitlab.northwind.dev", ProviderKind.GitLab),
            ClientId = "id", State = "s", CodeChallengeS256 = "c", RedirectUri = Redirect
        });

        url.AbsoluteUri.ShouldStartWith("https://gitlab.northwind.dev/oauth/authorize?");
    }

    [Fact]
    public void GitHub_kind_is_GitHub()
    {
        new GitHubOAuthClient(httpClientFactory: null!, TimeProvider.System).Kind.ShouldBe(ProviderKind.GitHub);
    }

    [Fact]
    public void GitLab_kind_is_GitLab()
    {
        new GitLabOAuthClient(httpClientFactory: null!, TimeProvider.System).Kind.ShouldBe(ProviderKind.GitLab);
    }

    private static ProviderInstance BuildInstance(string baseUrl, ProviderKind kind) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        Provider = kind,
        DisplayName = "test",
        BaseUrl = baseUrl
    };
}

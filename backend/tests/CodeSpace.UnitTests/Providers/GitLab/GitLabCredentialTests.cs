using System.Net;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Messages.Enums;
using NGitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// The credential value every GitLab call presents: which header its type names, that it replaces whatever credential header
/// a request already carries (NGitLab writes <c>Authorization: Bearer</c> before anything else sees the request), and that
/// formatting one never prints the token. The wire behaviour is pinned in <see cref="GitLabCredentialHeaderTests"/>.
/// </summary>
[Trait("Category", "Unit")]
public class GitLabCredentialTests
{
    private const string Token = "glpat-UNIT-CANARY-0123456789";

    [Theory]
    [InlineData(AuthType.Pat, "PRIVATE-TOKEN", Token)]
    [InlineData(AuthType.ProjectAccessToken, "PRIVATE-TOKEN", Token)]
    [InlineData(AuthType.GroupAccessToken, "PRIVATE-TOKEN", Token)]
    [InlineData(AuthType.OAuth, "Authorization", "Bearer " + Token)]
    public void The_credential_type_names_the_one_header_it_travels_in(AuthType type, string header, string value)
    {
        var credential = new GitLabCredential(Token, type);

        credential.HeaderName.ShouldBe(header);
        credential.HeaderValue.ShouldBe(value);
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.OAuth)]
    public void Attaching_replaces_any_credential_header_the_request_already_carries(AuthType type)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://gitlab.test/api/v4/user");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer stale");
        request.Headers.TryAddWithoutValidation("PRIVATE-TOKEN", "stale");

        new GitLabCredential(Token, type).Attach(request);

        request.Headers.Select(h => h.Key).Order().ShouldBe(type == AuthType.OAuth ? new[] { "Authorization" } : new[] { "PRIVATE-TOKEN" });
        request.Headers.GetValues(type == AuthType.OAuth ? "Authorization" : "PRIVATE-TOKEN").ShouldHaveSingleItem().ShouldContain(Token);
    }

    [Theory]
    [InlineData(AuthType.Pat)]
    [InlineData(AuthType.OAuth)]
    public void Attaching_to_the_headers_NGitLab_wrote_leaves_exactly_one_credential_header(AuthType type)
    {
        var headers = new WebHeaderCollection { { HttpRequestHeader.Authorization, "Bearer " + Token }, { HttpRequestHeader.AcceptEncoding, "gzip" } };

        new GitLabCredential(Token, type).Attach(headers);

        headers["Accept-Encoding"].ShouldBe("gzip", "what NGitLab set besides the credential is kept");
        (headers["Authorization"] is not null).ShouldBe(type == AuthType.OAuth);
        (headers["PRIVATE-TOKEN"] is not null).ShouldBe(type != AuthType.OAuth);
    }

    [Fact]
    public void Formatting_a_credential_never_prints_the_token()
    {
        var credential = new GitLabCredential(Token, AuthType.Pat);

        credential.ToString().ShouldNotContain(Token);
        $"{credential}".ShouldNotContain(Token);
    }

    [Fact]
    public void The_typed_clients_options_are_the_defaults_it_replaces()
    {
        // NGitLab's own default: no retries of its own (the resilience wrapper retries) and its five-minute timeout.
        var ours = new GitLabRequestOptions(new GitLabCredential(Token, AuthType.Pat), "https://gitlab.acme.test");
        var theirs = RequestOptions.Default;

        ours.RetryCount.ShouldBe(theirs.RetryCount);
        ours.RetryInterval.ShouldBe(theirs.RetryInterval);
        ours.IsIncremental.ShouldBe(theirs.IsIncremental);
        ours.HttpClientTimeout.ShouldBe(theirs.HttpClientTimeout);
        ours.Proxy.ShouldBe(theirs.Proxy);
        ours.UserAgent.ShouldBe(theirs.UserAgent);
    }
}

using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// The typed failure for a provider base URL that answers a redirect which is not followed: what it tells the owner to do,
/// and that the URLs it names are only ever scheme, host, port and path — the redirect's <c>Location</c> is the redirecting
/// server's to write, so a credential in its userinfo or query must never reach a message the owner reads or a log line.
/// </summary>
[Trait("Category", "Unit")]
public class ProviderBaseUrlRedirectsExceptionTests
{
    [Fact]
    public void A_redirect_to_another_origin_is_an_unprocessable_failure_that_names_where_the_provider_pointed()
    {
        var failure = new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, new Uri("http://gitlab.acme.test/api/v4/projects/42"), new Uri("https://gitlab.acme.test/api/v4/projects/42"));

        ((IFailure)failure).Kind.ShouldBe(FailureKind.Unprocessable);
        ((IFailure)failure).Code.ShouldBe("provider_base_url_redirects");
        failure.RequestedOrigin.ShouldBe("http://gitlab.acme.test");
        failure.FinalOrigin.ShouldBe("https://gitlab.acme.test");
        failure.FinalUrl.ShouldBe("https://gitlab.acme.test/api/v4/projects/42");
        failure.Message.ShouldContain("Edit the provider's base URL to the address GitLab answers on (https://gitlab.acme.test)");
        failure.Message.ShouldContain("only upgrades http to https on the same host is allowed", Case.Sensitive, "the self-service fix is named");

        var details = ((IFailure)failure).Details!;
        details["provider"].ShouldBe("GitLab");
        details["finalUrl"].ShouldBe(failure.FinalUrl);
        details["finalOrigin"].ShouldBe("https://gitlab.acme.test");
        details["requestedOrigin"].ShouldBe("http://gitlab.acme.test");
    }

    [Theory]
    [InlineData("https://svc:hunter2@gitlab.acme.test:8443/api/v4/projects/42?private_token=SECRET#frag", "https://gitlab.acme.test:8443/api/v4/projects/42", "https://gitlab.acme.test:8443")]
    [InlineData("https://gitlab.acme.test:443/a/b", "https://gitlab.acme.test/a/b", "https://gitlab.acme.test")]            // a scheme's own port is not spelled
    [InlineData("http://gitlab.acme.test:80/", "http://gitlab.acme.test/", "http://gitlab.acme.test")]
    [InlineData("http://GITLAB.acme.test:8080/x", "http://gitlab.acme.test:8080/x", "http://gitlab.acme.test:8080")]        // host case is not the owner's to match
    [InlineData("http://[::1]:8080/x", "http://[::1]:8080/x", "http://[::1]:8080")]
    public void The_url_it_names_is_scheme_host_port_and_path_alone(string location, string expectedUrl, string expectedOrigin)
    {
        var failure = new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, new Uri("http://127.0.0.1:9/api/v4/user"), new Uri(location));

        failure.FinalUrl.ShouldBe(expectedUrl);
        failure.FinalOrigin.ShouldBe(expectedOrigin);
        failure.Message.ShouldNotContain("hunter2");
        failure.Message.ShouldNotContain("SECRET");
        failure.Message.ShouldNotContain("frag");
        ((IFailure)failure).Details!.Values.OfType<string>().ShouldAllBe(value => !value.Contains("hunter2") && !value.Contains("SECRET"));
    }

    [Fact]
    public void A_page_link_to_another_origin_reads_as_a_redirect_or_link_that_was_not_followed()
    {
        // NGitLab takes the next page of a list from a Link header, which is the answering server's to write like a Location.
        var failure = new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, new Uri("http://gitlab.acme.test"), new Uri("http://collector.test/api/v4/projects/42/repository/branches?page=2"));

        failure.Message.ShouldContain("redirect or link");
        failure.Message.ShouldContain("http://collector.test/api/v4/projects/42/repository/branches");
        failure.Message.ShouldNotContain("page=2");
    }

    [Fact]
    public void A_redirect_the_git_transport_met_names_the_git_host_when_no_provider_is_known()
    {
        // git's own transport does not know which provider a remote belongs to: it knows the origin it was sent to and asked for
        // a credential at.
        var failure = new ProviderBaseUrlRedirectsException(new Uri("http://127.0.0.1:18102/g/r.git"), new Uri("https://127.0.0.1:18443"));

        failure.ProviderKind.ShouldBeNull();
        failure.RequestedOrigin.ShouldBe("http://127.0.0.1:18102");
        failure.FinalOrigin.ShouldBe("https://127.0.0.1:18443");
        failure.Message.ShouldStartWith("The git host answered");
        failure.Message.ShouldContain("Edit the provider's base URL to the address the git host answers on (https://127.0.0.1:18443)");
        ((IFailure)failure).Code.ShouldBe("provider_base_url_redirects");
        ((IFailure)failure).Details!.ContainsKey("provider").ShouldBeFalse("no provider is known, so none is claimed");
        ((IFailure)failure).Details!["finalOrigin"].ShouldBe("https://127.0.0.1:18443");
    }

    [Fact]
    public void A_redirect_that_stays_inside_one_origin_is_named_as_one_that_was_not_followed_not_as_another_origin()
    {
        // Not followed all the same: a write is never sent again blindly, and a redirect that does not end is not a route.
        var failure = new ProviderBaseUrlRedirectsException(ProviderKind.GitLab, new Uri("https://gitlab.acme.test/api/v4/projects/42/issues"), new Uri("https://gitlab.acme.test/other/issues"));

        failure.Message.ShouldContain("not followed");
        failure.Message.ShouldContain("never sent again blindly");
        failure.Message.ShouldNotContain("another origin");
    }
}

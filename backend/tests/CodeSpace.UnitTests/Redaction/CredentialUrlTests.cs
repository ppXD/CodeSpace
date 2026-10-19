using CodeSpace.Core.Services.Redaction;
using Shouldly;

namespace CodeSpace.UnitTests.Redaction;

/// <summary>
/// Pins the display form of a credential-bearing URL (userinfo and secret-named query values masked, everything else
/// verbatim) and the endpoint identity a stored key is bound to (scheme, host, port).
/// </summary>
[Trait("Category", "Unit")]
public class CredentialUrlTests
{
    [Fact]
    public void Mask_is_pinned() => CredentialUrl.Mask.ShouldBe("***");

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("https://gw.example/v1", "https://gw.example/v1")]
    [InlineData("https://GW.example:443/v1/", "https://GW.example:443/v1/")]
    [InlineData("http://localhost:11434", "http://localhost:11434")]
    [InlineData("https://usr:CANARY-PW@gw.example/v1", "https://***:***@gw.example/v1")]
    [InlineData("https://CANARY-TOKEN@gw.example/v1", "https://***@gw.example/v1")]
    [InlineData("https://gw.example/v1?api-key=CANARY-Q&api-version=2024-01-01", "https://gw.example/v1?api-key=***&api-version=2024-01-01")]
    [InlineData("https://gw.example/v1?key=CANARY-Q#frag", "https://gw.example/v1?key=***#frag")]
    [InlineData("https://gw.example?api%2Dkey=CANARY-Q", "https://gw.example?api%2Dkey=***")]
    [InlineData("https://gw.example/v1?token=", "https://gw.example/v1?token=")]
    [InlineData("https://u:CANARY-PW@gw.example:8443/v1?model=m&access_token=CANARY-Q", "https://***:***@gw.example:8443/v1?model=m&access_token=***")]
    [InlineData("https://gw.example/v1/CANARY-PATH/chat", "https://gw.example/v1/CANARY-PATH/chat")]
    public void Masked_hides_userinfo_and_secret_query_values_and_keeps_the_rest_verbatim(string? stored, string? expected) => CredentialUrl.Masked(stored).ShouldBe(expected);

    [Fact]
    public void Masked_never_contains_the_embedded_credentials()
    {
        var masked = CredentialUrl.Masked("https://usr:CANARY-PW-1@gw.example/v1?api-key=CANARY-Q-2&secret=CANARY-Q-3");

        masked.ShouldNotContain("CANARY");
        masked.ShouldContain("gw.example/v1");
    }

    [Theory]
    [InlineData("https://***:***@gw.example/v1", true)]
    [InlineData("https://usr:***@gw.example/v1", true)]
    [InlineData("https://gw.example/v2?api-key=***", true)]
    [InlineData("https://gw.example/v1?note=***", false)]
    [InlineData("https://usr:pw@gw.example/v1?api-key=abc", false)]
    [InlineData("https://gw.example/v1", false)]
    [InlineData(null, false)]
    public void CarriesMask_finds_the_placeholder_in_a_credential_slot(string? url, bool expected) => CredentialUrl.CarriesMask(url).ShouldBe(expected);

    [Theory]
    [InlineData("https://gw.example/v1", "https://gw.example/v2", true)]
    [InlineData("https://gw.example/v1", "https://GW.EXAMPLE/v1", true)]
    [InlineData("https://gw.example/v1", "https://gw.example:443/v1", true)]
    [InlineData("https://a:b@gw.example/v1", "https://c:d@gw.example/v1?api-key=x", true)]
    [InlineData(null, null, true)]
    [InlineData("", null, true)]
    [InlineData("https://gw.example/v1", "https://evil.example/v1", false)]
    [InlineData("https://gw.example/v1", "http://gw.example/v1", false)]
    [InlineData("https://gw.example/v1", "https://gw.example:8443/v1", false)]
    [InlineData(null, "https://gw.example/v1", false)]
    [InlineData("https://gw.example/v1", null, false)]
    [InlineData("gw.example:4000", "gw.example:5000", false)]
    [InlineData("gw.example:4000", "gw.example:4000", true)]
    public void SameEndpoint_compares_scheme_host_and_port(string? stored, string? next, bool expected) => CredentialUrl.SameEndpoint(stored, next).ShouldBe(expected);

    [Theory]
    [InlineData("https://gitlab.acme.test", "https://gitlab.acme.test", true)]
    [InlineData("https://gitlab.acme.test", "https://gitlab.acme.test/", true)]                  // a trailing slash is not a path
    [InlineData("https://gitlab.acme.test/gitlab", "https://gitlab.acme.test/gitlab/", true)]
    [InlineData("https://GITLAB.acme.test:443/gitlab", "https://gitlab.acme.test/gitlab", true)] // host case and the default port, as SameEndpoint reads them
    [InlineData("https://a:b@gitlab.acme.test/gitlab", "https://gitlab.acme.test/gitlab?x=1", true)] // userinfo and query are not part of the root
    [InlineData(null, null, true)]
    [InlineData("https://gitlab.acme.test", "https://gitlab.acme.test/gitlab", false)]          // a GitLab under /gitlab is not the one at the root
    [InlineData("https://gitlab.acme.test/gitlab", "https://gitlab.acme.test/other", false)]
    [InlineData("https://gitlab.acme.test/GitLab", "https://gitlab.acme.test/gitlab", false)]    // a path is case-sensitive
    [InlineData("https://gitlab.acme.test", "https://gitlab.acme.test:8443", false)]             // everything SameEndpoint refuses, SameRoot refuses
    [InlineData("https://gitlab.acme.test", "http://gitlab.acme.test", false)]
    [InlineData("https://gitlab.acme.test", "https://attacker.test", false)]
    [InlineData("https://gitlab.acme.test", null, false)]
    [InlineData("gitlab.acme.test:4000", "gitlab.acme.test:4000", true)]                        // not an absolute http(s) URL: it only matches itself
    [InlineData("gitlab.acme.test:4000", "gitlab.acme.test:5000", false)]
    public void SameRoot_is_the_same_endpoint_and_the_same_path(string? a, string? b, bool expected)
    {
        CredentialUrl.SameRoot(a, b).ShouldBe(expected);
        CredentialUrl.SameRoot(b, a).ShouldBe(expected, "symmetric");
    }

    [Theory]
    [InlineData("https://gitlab.acme.test/api/v4", "https://gitlab.acme.test:443/api/v4/")]
    [InlineData("https://gitlab.acme.test", "https://gitlab.acme.test/")]
    public void SameRoot_agrees_with_SameEndpoint_wherever_the_endpoint_is_the_same_and_the_path_is_(string a, string b)
    {
        CredentialUrl.SameEndpoint(a, b).ShouldBeTrue("fixture check: the endpoint is the same");
        CredentialUrl.SameRoot(a, b).ShouldBeTrue();
    }

    [Theory]
    [InlineData("http://gw.example", "https://gw.example", true)]                       // the default ports, 80 to 443
    [InlineData("http://gw.example:80", "https://gw.example:443", true)]                // spelled out
    [InlineData("http://gw.example:8080", "https://gw.example:8080", true)]             // the same explicit port
    [InlineData("http://GW.example/v1", "https://gw.EXAMPLE/other?x=1", true)]          // host case, path and query are not part of it
    [InlineData("http://a:b@gw.example", "https://gw.example", true)]
    [InlineData("http://gw.example", "https://gw.example:8443", false)]                 // another port
    [InlineData("http://gw.example:8080", "https://gw.example", false)]
    [InlineData("http://gw.example:8080", "https://gw.example:8443", false)]
    [InlineData("http://gw.example:443", "https://gw.example", false)]                  // an explicit 443 on http is not the https default
    [InlineData("http://gw.example", "https://gw.example:80", false)]
    [InlineData("https://gw.example", "http://gw.example", false)]                      // a downgrade
    [InlineData("https://gw.example", "https://gw.example", false)]                     // not a move at all
    [InlineData("http://gw.example", "http://gw.example", false)]
    [InlineData("http://gw.example", "https://gw2.example", false)]                     // another host, an alias included
    [InlineData("http://gw.example", "https://gw.example.evil.example", false)]
    [InlineData("http://gw.example", "ftp://gw.example", false)]
    [InlineData("gw.example:4000", "https://gw.example:4000", false)]                   // not an absolute http(s) URL
    [InlineData(null, "https://gw.example", false)]
    [InlineData("http://gw.example", null, false)]
    [InlineData("", "", false)]
    public void IsHttpsUpgrade_is_the_same_host_and_port_moved_from_http_to_https(string? stored, string? next, bool expected) => CredentialUrl.IsHttpsUpgrade(stored, next).ShouldBe(expected);

    private const string StoredWithCredentials = "https://svc:CANARY-PW-11@gw.example/v1?api-key=CANARY-Q-22&api-version=2024-01-01";

    [Theory]
    [InlineData(StoredWithCredentials, "https://***:***@gw.example/v1?api-key=***&api-version=2024-01-01", StoredWithCredentials)]
    [InlineData(StoredWithCredentials, StoredWithCredentials, StoredWithCredentials)]
    [InlineData(StoredWithCredentials, "https://gw.example/v2", "https://gw.example/v2")]
    [InlineData(StoredWithCredentials, "https://new:CANARY-NEW@gw.example/v1", "https://new:CANARY-NEW@gw.example/v1")]
    [InlineData("http://localhost:11434", "http://localhost:11434", "http://localhost:11434")]
    [InlineData(null, "https://gw.example/v1", "https://gw.example/v1")]
    [InlineData("https://gw.example/v1", "", "")]
    [InlineData("https://gw.example/v1", null, null)]
    public void KeepStoredIfMasked_reads_the_display_form_sent_back_as_unchanged(string? stored, string? submitted, string? expected) => CredentialUrl.KeepStoredIfMasked(stored, submitted).ShouldBe(expected);

    [Theory]
    [InlineData("https://***:***@gw.example/v2?api-key=***&api-version=2024-01-01")]
    [InlineData("https://***:***@evil.example/v1?api-key=***&api-version=2024-01-01")]
    [InlineData("https://svc:***@gw.example/v1")]
    public void KeepStoredIfMasked_refuses_a_placeholder_left_in_an_edited_url(string submitted)
    {
        var refusal = Should.Throw<ArgumentException>(() => CredentialUrl.KeepStoredIfMasked(StoredWithCredentials, submitted));

        refusal.Message.ShouldContain(CredentialUrl.Mask);
        refusal.Message.ShouldNotContain("CANARY");
    }

    [Theory]
    [InlineData(null, "(provider default)")]
    [InlineData("https://usr:CANARY@gw.example/v1?api-key=CANARY", "https://gw.example:443")]
    [InlineData("http://10.0.0.5:4000/v1", "http://10.0.0.5:4000")]
    [InlineData("not a url CANARY", "(unparseable)")]
    public void Endpoint_names_only_scheme_host_and_port(string? url, string expected) => CredentialUrl.Endpoint(url).ShouldBe(expected);
}

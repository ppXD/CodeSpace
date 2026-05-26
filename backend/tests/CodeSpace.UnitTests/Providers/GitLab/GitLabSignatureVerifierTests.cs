using CodeSpace.Core.Services.Providers.GitLab;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitLab;

public class GitLabSignatureVerifierTests
{
    private readonly GitLabSignatureVerifier _verifier = new();
    private const string Secret = "gitlab-webhook-secret";

    [Fact]
    public void Verify_returns_true_when_token_matches()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Token"] = Secret };

        _verifier.Verify("any body", headers, Secret).ShouldBeTrue();
    }

    [Fact]
    public void Verify_returns_false_when_token_differs()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Token"] = "wrong-token" };

        _verifier.Verify("any body", headers, Secret).ShouldBeFalse();
    }

    [Fact]
    public void Verify_returns_false_when_header_missing()
    {
        var headers = new Dictionary<string, string>();

        _verifier.Verify("any body", headers, Secret).ShouldBeFalse();
    }

    [Theory]
    [InlineData("X-Gitlab-Token")]
    [InlineData("x-gitlab-token")]
    [InlineData("X-GITLAB-TOKEN")]
    public void Verify_header_lookup_is_case_insensitive(string headerName)
    {
        var headers = new Dictionary<string, string> { [headerName] = Secret };

        _verifier.Verify("any body", headers, Secret).ShouldBeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_short_mismatched_tokens()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Token"] = "x" };

        _verifier.Verify("any", headers, "y").ShouldBeFalse();
    }

    [Fact]
    public void Verify_returns_false_when_provided_token_is_empty()
    {
        var headers = new Dictionary<string, string> { ["X-Gitlab-Token"] = string.Empty };

        _verifier.Verify("any", headers, Secret).ShouldBeFalse();
    }

    [Fact]
    public void Verify_handles_unicode_secret()
    {
        var unicodeSecret = "密鑰-🔑-test";
        var headers = new Dictionary<string, string> { ["X-Gitlab-Token"] = unicodeSecret };

        _verifier.Verify("any", headers, unicodeSecret).ShouldBeTrue();
    }
}

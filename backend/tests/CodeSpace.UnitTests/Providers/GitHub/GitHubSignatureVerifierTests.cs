using System.Security.Cryptography;
using System.Text;
using CodeSpace.Core.Services.Providers.GitHub;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.GitHub;

public class GitHubSignatureVerifierTests
{
    private readonly GitHubSignatureVerifier _verifier = new();
    private const string Secret = "test-webhook-secret-do-not-use-in-prod";

    [Fact]
    public void Verify_returns_true_when_signature_matches()
    {
        var body = "{\"hello\":\"world\"}";
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = ComputeExpected(body, Secret)
        };

        _verifier.Verify(body, headers, Secret).ShouldBeTrue();
    }

    [Fact]
    public void Verify_returns_false_when_signature_mismatched()
    {
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = "sha256=ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff"
        };

        _verifier.Verify("{\"hello\":\"world\"}", headers, Secret).ShouldBeFalse();
    }

    [Fact]
    public void Verify_returns_false_when_header_missing()
    {
        var headers = new Dictionary<string, string>();

        _verifier.Verify("{\"hello\":\"world\"}", headers, Secret).ShouldBeFalse();
    }

    [Fact]
    public void Verify_returns_false_when_header_has_no_prefix()
    {
        var headers = new Dictionary<string, string> { ["X-Hub-Signature-256"] = "garbage" };

        _verifier.Verify("{}", headers, Secret).ShouldBeFalse();
    }

    [Fact]
    public void Verify_returns_false_when_secret_differs()
    {
        var body = "{}";
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = ComputeExpected(body, Secret)
        };

        _verifier.Verify(body, headers, "different-secret").ShouldBeFalse();
    }

    [Fact]
    public void Verify_returns_false_when_body_tampered()
    {
        var originalBody = "{\"hello\":\"world\"}";
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = ComputeExpected(originalBody, Secret)
        };

        _verifier.Verify("{\"hello\":\"changed\"}", headers, Secret).ShouldBeFalse();
    }

    [Theory]
    [InlineData("X-Hub-Signature-256")]
    [InlineData("x-hub-signature-256")]
    [InlineData("X-HUB-SIGNATURE-256")]
    public void Verify_header_lookup_is_case_insensitive(string headerName)
    {
        var body = "{}";
        var headers = new Dictionary<string, string> { [headerName] = ComputeExpected(body, Secret) };

        _verifier.Verify(body, headers, Secret).ShouldBeTrue();
    }

    [Fact]
    public void Verify_returns_true_for_empty_body_with_correct_signature()
    {
        var body = "";
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = ComputeExpected(body, Secret)
        };

        _verifier.Verify(body, headers, Secret).ShouldBeTrue();
    }

    [Fact]
    public void Verify_handles_unicode_body()
    {
        var body = "{\"name\":\"用戶測試\",\"emoji\":\"🚀\"}";
        var headers = new Dictionary<string, string>
        {
            ["X-Hub-Signature-256"] = ComputeExpected(body, Secret)
        };

        _verifier.Verify(body, headers, Secret).ShouldBeTrue();
    }

    private static string ComputeExpected(string body, string secret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(body));
        return $"sha256={Convert.ToHexString(hash).ToLowerInvariant()}";
    }
}

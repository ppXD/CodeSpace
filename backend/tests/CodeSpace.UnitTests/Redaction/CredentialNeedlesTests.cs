using System.Net.Http.Headers;
using CodeSpace.Core.Services.Agents;
using CodeSpace.Core.Services.Redaction;
using CodeSpace.Messages.Agents;
using Shouldly;

namespace CodeSpace.UnitTests.Redaction;

/// <summary>
/// Pins the shared needle rule: what a credential, a URL and an outgoing request PRESENT, so every echo scrub (the agent
/// run's redactor, the in-process model transport, the git-provider error translation) strikes the same values.
/// </summary>
[Trait("Category", "Unit")]
public class CredentialNeedlesTests
{
    private const string Key = "sk-NEEDLE-0f9e8d7c6b5a";

    [Fact]
    public void MinimumLength_is_pinned() => CredentialNeedles.MinimumLength.ShouldBe(8);

    [Theory]
    [InlineData("API_KEY", true)]
    [InlineData("api-key", true)]
    [InlineData("x-api-key", true)]
    [InlineData("Authorization", true)]
    [InlineData("PRIVATE-TOKEN", true)]
    [InlineData("access_token", true)]
    [InlineData("api-version", false)]
    [InlineData("anthropic-version", false)]
    [InlineData("model", false)]
    public void MarksASecret_reads_the_name(string name, bool expected) => CredentialNeedles.MarksASecret(name).ShouldBe(expected);

    [Fact]
    public void FromUrl_yields_userinfo_and_secret_query_values_in_both_spellings()
    {
        var needles = CredentialNeedles.FromUrl("https://svc:p%40ss-word-123@gw.example/v1?api-key=q%2Bkey-456789&api-version=2024-01-01").ToList();

        needles.ShouldContain("svc");
        needles.ShouldContain("p%40ss-word-123");
        needles.ShouldContain("p@ss-word-123");
        needles.ShouldContain("q%2Bkey-456789");
        needles.ShouldContain("q+key-456789");
        needles.ShouldNotContain("2024-01-01", "a non-secret query value stays readable");
        needles.ShouldNotContain(n => n.Contains("gw.example"), "the host is what says which endpoint answered");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("https://gw.example/v1")]
    public void FromUrl_yields_nothing_when_the_url_carries_no_credential(string? url) => CredentialNeedles.FromUrl(url).ShouldBeEmpty();

    [Fact]
    public void FromCredential_yields_the_key_then_the_url_parts()
    {
        var needles = CredentialNeedles.FromCredential(new ResolvedModelCredential { Provider = "OpenAI", ApiKey = Key, BaseUrl = "https://gw.example/v1?token=tok-123456789" }).ToList();

        needles[0].ShouldBe(Key);
        needles.ShouldContain("tok-123456789");
    }

    [Fact]
    public void FromRequest_yields_the_bearer_credential_not_the_scheme()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://gw.example/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Key);

        var needles = CredentialNeedles.FromRequest(request).ToList();

        needles.ShouldContain(Key);
        needles.ShouldNotContain("Bearer");
    }

    [Theory]
    [InlineData("x-api-key")]
    [InlineData("api-key")]
    [InlineData("PRIVATE-TOKEN")]
    public void FromRequest_yields_a_secret_named_header_value(string header)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://gw.example/v1/messages");
        request.Headers.Add(header, Key);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var needles = CredentialNeedles.FromRequest(request).ToList();

        needles.ShouldContain(Key);
        needles.ShouldNotContain("2023-06-01", "a version header is not a credential");
    }

    [Fact]
    public void FromRequest_yields_the_credential_parts_of_the_request_url()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "https://svc:pw-0123456789@gw.example/v1/chat/completions?api-key=q-0123456789");

        var needles = CredentialNeedles.FromRequest(request).ToList();

        needles.ShouldContain("pw-0123456789");
        needles.ShouldContain("q-0123456789");
    }

    [Fact]
    public void Redactor_drops_fragments_below_the_minimum_length()
    {
        var redactor = CredentialNeedles.Redactor(["EMPTY", null, Key]);

        redactor.Redact($"EMPTY key {Key} rejected").ShouldBe($"EMPTY key {SecretRedactor.Placeholder} rejected");
    }

    [Fact]
    public void Redactor_with_no_usable_needle_is_the_identity() => CredentialNeedles.Redactor(["short", null]).ShouldBeSameAs(SecretRedactor.None);

    [Theory]
    [InlineData("{\"message\":\"401 Unauthorized (PRIVATE-TOKEN=sk-NEEDLE-0f9e8d7c6b5a)\"}", "{\"message\":\"401 Unauthorized (PRIVATE-TOKEN=***)\"}")]
    [InlineData("Bearer sk-NEEDLE-0f9e8d7c6b5a, again sk-NEEDLE-0f9e8d7c6b5a", "Bearer ***, again ***")]
    [InlineData("403 Forbidden", "403 Forbidden")]
    [InlineData("", "")]
    [InlineData(null, null)]
    public void Strike_removes_every_presented_credential_from_an_answer_and_keeps_the_rest(string? answer, string? expected) => CredentialNeedles.Strike(answer, [Key]).ShouldBe(expected);

    [Fact]
    public void Strike_with_nothing_presented_is_the_identity() => CredentialNeedles.Strike("401 (PRIVATE-TOKEN=short)", [null, "short"]).ShouldBe("401 (PRIVATE-TOKEN=short)");
}

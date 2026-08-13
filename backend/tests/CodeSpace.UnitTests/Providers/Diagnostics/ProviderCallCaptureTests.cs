using System;
using System.Collections.Generic;
using System.Linq;
using CodeSpace.Core.Services.Providers.Diagnostics;
using Shouldly;

namespace CodeSpace.UnitTests.Providers.Diagnostics;

/// <summary>
/// Pins the masking that stands between a failed webhook registration and a database row holding a
/// working credential. Every persisted diagnostic passes through <see cref="ProviderCallCapture"/>,
/// so if these rules are wrong, the leak is already written by the time anyone looks.
///
/// <para>The webhook secret and the provider credential are covered by three independent rules —
/// header name, JSON field name, literal value — and each is pinned separately, so a change that
/// breaks one cannot hide behind the other two.</para>
/// </summary>
[Trait("Category", "Unit")]
public class ProviderCallCaptureTests
{
    private const string WebhookSecret = "whsec-8f3a1c9d2b";
    private const string CredentialToken = "glpat-EXAMPLEtoken1234";

    [Fact]
    public void A_captured_request_carries_neither_the_webhook_secret_nor_the_credential()
    {
        // The GitLab shape: the secret rides in the body under `token`, the credential rides in the
        // Authorization header, and a caller could put the credential in the query string too.
        var body = $$"""{"url":"https://cs.local/api/webhooks/abc","push_events":true,"token":"{{WebhookSecret}}"}""";
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {CredentialToken}", ["Content-Type"] = "application/json" };

        var captured = ProviderCallCapture.CaptureRedacted("POST", $"https://gitlab.local/api/v4/projects/42/hooks?private_token={CredentialToken}", headers, body, new[] { CredentialToken, WebhookSecret });

        var everythingPersisted = string.Join("\n", new[] { captured.Method, captured.Url, captured.Body }.Concat(captured.Headers.Select(h => $"{h.Key}: {h.Value}")));

        everythingPersisted.ShouldNotContain(WebhookSecret,
            customMessage: "The webhook secret reached the captured request. Masking happens here or not at all — a row written with it is already leaked.");
        everythingPersisted.ShouldNotContain(CredentialToken,
            customMessage: "The provider credential reached the captured request. Masking happens here or not at all — a row written with it is already leaked.");
    }

    [Fact]
    public void The_request_stays_legible_after_masking()
    {
        // Masking that also erased WHAT WE ASKED would defeat the point: the operator needs to see
        // the route, the method, and the non-secret fields to tell one failure from another.
        var body = $$"""{"url":"https://cs.local/api/webhooks/abc","push_events":true,"token":"{{WebhookSecret}}"}""";
        var headers = new Dictionary<string, string> { ["Authorization"] = $"Bearer {CredentialToken}" };

        var captured = ProviderCallCapture.CaptureRedacted("POST", "https://gitlab.local/api/v4/projects/42/hooks", headers, body, new[] { CredentialToken, WebhookSecret });

        captured.Method.ShouldBe("POST");
        captured.Url.ShouldBe("https://gitlab.local/api/v4/projects/42/hooks");
        captured.Body.ShouldContain("push_events");
        captured.Body.ShouldContain("https://cs.local/api/webhooks/abc");
        captured.Headers["Authorization"].ShouldBe(ProviderCallCapture.Mask);
    }

    [Fact]
    public void A_secret_bearing_field_is_masked_even_when_nobody_declared_its_value()
    {
        // The rule that survives the future: a field added next year, whose value nobody remembered
        // to pass in as a secret, is still masked because of what it is CALLED. GitHub's shape
        // exercises the nesting — its secret sits under `config`, not at the top level.
        var body = """{"name":"web","config":{"url":"https://cs.local/hook","secret":"undeclared-secret"},"active":true}""";

        var captured = ProviderCallCapture.CaptureRedacted("POST", "https://api.github.com/repositories/99/hooks", new Dictionary<string, string>(), body, Array.Empty<string>());

        captured.Body.ShouldNotContain("undeclared-secret",
            customMessage: "A nested secret-named field was persisted verbatim — the field-name rule must recurse, because GitHub nests the webhook secret under `config`.");
        captured.Body.ShouldContain("https://cs.local/hook",
            customMessage: "Only the secret field should be masked; the rest of the payload is what makes the row useful.");
    }

    [Theory]
    [InlineData("PRIVATE-TOKEN")]
    [InlineData("authorization")]
    [InlineData("Cookie")]
    public void A_credential_carrying_header_is_masked_by_its_name(string headerName)
    {
        // Header names are case-insensitive on the wire, so the rule has to be too — otherwise the
        // masking depends on which SDK happened to capitalise which way.
        var captured = ProviderCallCapture.CaptureRedacted("GET", "https://gitlab.local/api/v4/projects/42/hooks", new Dictionary<string, string> { [headerName] = CredentialToken }, null, Array.Empty<string>());

        captured.Headers[headerName].ShouldBe(ProviderCallCapture.Mask);
    }

    [Fact]
    public void A_non_json_body_still_loses_its_secrets()
    {
        // The field-name rule cannot read a form-encoded body; the literal scrub is what covers it.
        var captured = ProviderCallCapture.CaptureRedacted("POST", "https://gitlab.local/api/v4/projects/42/hooks", new Dictionary<string, string>(), $"url=https://cs.local/hook&token={WebhookSecret}", new[] { WebhookSecret });

        captured.Body.ShouldNotContain(WebhookSecret);
        captured.Body.ShouldContain("url=https://cs.local/hook");
    }

    [Fact]
    public void An_oversized_body_is_clamped_and_says_so()
    {
        // A misconfigured reverse proxy answers with a megabyte of HTML. Ten of those would make the
        // diagnostic table larger than everything it diagnoses, and the first 4000 chars say as much.
        var clamped = ProviderCallCapture.Clamp(new string('x', ProviderCallCapture.MaxBodyChars + 500));

        clamped!.Length.ShouldBeLessThan(ProviderCallCapture.MaxBodyChars + 100);
        clamped.ShouldContain("truncated",
            customMessage: "A clamped body must say it was clamped, or the next reader debugs the truncation as a malformed response.");
    }

    [Fact]
    public void A_body_within_the_cap_is_left_exactly_as_it_came()
    {
        var body = new string('x', ProviderCallCapture.MaxBodyChars);

        ProviderCallCapture.Clamp(body).ShouldBe(body);
    }

    [Fact]
    public void The_sdk_exception_is_found_underneath_the_layers_that_wrapped_it()
    {
        // Nothing in the diagnostic works if this does not: by the time the provider catches, the
        // resilience layer has translated the SDK exception and the scope mapper may have replaced
        // it outright. The status and body only exist on the original.
        var original = new InvalidOperationException("what the provider actually said");
        var wrapped = new Exception("outer", new Exception("middle", original));

        ProviderCallCapture.FindInChain<InvalidOperationException>(wrapped).ShouldBeSameAs(original);
        ProviderCallCapture.FindInChain<TimeoutException>(wrapped).ShouldBeNull();
    }

    /// <summary>
    /// The worst case a reviewer should construct: a provider that echoes the request straight back inside its
    /// error body. GitLab does something close to this on a 400, quoting the offending field.
    /// </summary>
    [Fact]
    public void A_provider_that_echoes_the_request_back_cannot_leak_the_secret_through_the_response()
    {
        const string secret = "s3cr3t-webhook-token-abcdef";

        var echoed = ProviderCallCapture.Clamp(ScrubForTest("{\"error\":\"invalid hook\",\"sent\":{\"token\":\"" + secret + "\",\"url\":\"https://x\"}}", secret));

        echoed.ShouldNotContain(secret, customMessage: "a response body is provider-controlled — it must go through the same scrub as the request");
    }

    [Theory]
    [InlineData("https://gitlab.com/api/v4/projects/1/hooks?private_token=s3cr3t-webhook-token-abcdef")]
    [InlineData("https://gitlab.com/api/v4/projects/1/hooks#s3cr3t-webhook-token-abcdef")]
    public void A_secret_smuggled_into_the_url_is_scrubbed_too(string url)
    {
        const string secret = "s3cr3t-webhook-token-abcdef";

        var captured = ProviderCallCapture.CaptureRedacted("POST", url, new Dictionary<string, string>(), body: null, secrets: [secret]);

        captured.Url.ShouldNotContain(secret, customMessage: "neither the header rule nor the JSON-key rule looks at a URL; the literal scrub is what covers it");
    }

    [Fact]
    public void A_secret_nested_where_no_rule_names_it_is_still_scrubbed()
    {
        const string secret = "s3cr3t-webhook-token-abcdef";

        var captured = ProviderCallCapture.CaptureRedacted(
            "POST", "https://api.github.com/repos/o/r/hooks",
            new Dictionary<string, string> { ["X-Custom-Trace"] = $"replaying {secret}" },
            body: "{\"config\":{\"deeply\":{\"unnamed_field\":\"" + secret + "\"}}}",
            secrets: [secret]);

        captured.Body.ShouldNotContain(secret);
        captured.Headers["X-Custom-Trace"].ShouldNotContain(secret, customMessage: "an unrecognised header still gets its values scrubbed");
    }

    private static string ScrubForTest(string text, string secret) =>
        ProviderCallCapture.CaptureRedacted("POST", "https://x", new Dictionary<string, string>(), text, [secret]).Body!;
}

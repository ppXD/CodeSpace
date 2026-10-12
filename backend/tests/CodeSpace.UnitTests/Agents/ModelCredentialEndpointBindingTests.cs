using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Agents;

/// <summary>
/// Pins the two rules that bind a model credential's stored key to the endpoint it was entered for: the base URL an
/// edit submits (the masked display form sent back means "unchanged"), and when an endpoint change needs the key again.
/// The DB round-trips are integration-pinned in <c>ModelCredentialFlowTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class ModelCredentialEndpointBindingTests
{
    private const string Stored = "https://svc:CANARY-PW-11@gw.example/v1?api-key=CANARY-Q-22&api-version=2024-01-01";

    [Fact]
    public void The_masked_display_form_sent_back_keeps_the_stored_url() =>
        ModelCredentialService.SubmittedBaseUrl(Stored, "https://***:***@gw.example/v1?api-key=***&api-version=2024-01-01").ShouldBe(Stored);

    [Theory]
    [InlineData(Stored, Stored)]
    [InlineData("https://gw.example/v2", "https://gw.example/v2")]
    [InlineData("https://new:CANARY-NEW@gw.example/v1", "https://new:CANARY-NEW@gw.example/v1")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void Any_other_value_is_taken_as_submitted(string? submitted, string? expected) => ModelCredentialService.SubmittedBaseUrl(Stored, submitted).ShouldBe(expected);

    [Fact]
    public void A_url_with_nothing_to_mask_round_trips_as_itself() => ModelCredentialService.SubmittedBaseUrl("http://localhost:11434", "http://localhost:11434").ShouldBe("http://localhost:11434");

    [Theory]
    [InlineData("https://***:***@gw.example/v2?api-key=***&api-version=2024-01-01")]
    [InlineData("https://***:***@evil.example/v1?api-key=***&api-version=2024-01-01")]
    [InlineData("https://svc:***@gw.example/v1")]
    public void A_masked_placeholder_left_in_an_edited_url_is_refused_rather_than_stored_as_the_credential(string submitted)
    {
        var refusal = Should.Throw<ArgumentException>(() => ModelCredentialService.SubmittedBaseUrl(Stored, submitted));

        refusal.Message.ShouldContain("***");
        refusal.Message.ShouldNotContain("CANARY");
    }

    [Theory]
    [InlineData(true, "https://gw.example/v1", "https://evil.example/v1", null, true)]
    [InlineData(true, "https://gw.example/v1", "http://gw.example/v1", "", true)]
    [InlineData(true, "https://gw.example/v1", "https://gw.example:8443/v1", "  ", true)]
    [InlineData(true, null, "https://evil.example/v1", null, true)]
    [InlineData(true, "https://gw.example/v1", null, null, true)]
    [InlineData(true, "https://gw.example/v1", "https://gw.example/v2", null, false)]
    [InlineData(true, "https://gw.example/v1", "https://GW.example:443/v1", null, false)]
    [InlineData(true, "https://gw.example/v1", "https://evil.example/v1", "sk-new-key-0123", false)]
    [InlineData(false, "https://gw.example/v1", "https://evil.example/v1", null, false)]
    public void An_endpoint_move_needs_the_key_again_only_when_a_stored_key_would_follow_it(bool hasStoredKey, string? storedUrl, string? nextUrl, string? apiKey, bool expected) =>
        ModelCredentialService.KeyMustBeReentered(hasStoredKey, storedUrl, nextUrl, apiKey).ShouldBe(expected);

    [Fact]
    public void The_refusal_is_a_typed_invalid_request_with_a_pinned_code()
    {
        var id = Guid.NewGuid();
        var refusal = new ModelEndpointKeyRequiredException(id);

        ((IFailure)refusal).Kind.ShouldBe(FailureKind.Invalid);
        ((IFailure)refusal).Code.ShouldBe("model_endpoint_key_required");
        ((IFailure)refusal).Details!["credentialId"].ShouldBe(id);
    }
}

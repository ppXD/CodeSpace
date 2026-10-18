using CodeSpace.Core.Services.OAuth;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// Which of a provider's refusals of a refresh is a verdict on the grant — the one the refresher turns into a credential that
/// needs reconnecting — and the text a refusal carries once it names the credential it was for.
/// </summary>
[Trait("Category", "Unit")]
public class OAuthExchangeExceptionTests
{
    [Fact]
    public void The_refusals_that_judge_the_grant_or_the_client_are_pinned_member_by_member()
    {
        // Too wide and a provider that is merely down — or a request CodeSpace got wrong — marks a working credential as
        // dead and stops every run through it; too narrow and a dead grant stays Active and fails every run at the provider.
        // Either way the drift is silent, so the set is spelled out here as literals rather than compared with its source.
        OAuthExchangeException.FinalRefusalErrors.ShouldBe(new[] { "invalid_grant", "invalid_client", "unauthorized_client", "bad_refresh_token", "incorrect_client_credentials" }, ignoreOrder: true,
            customMessage: "adding an error code here makes the credential Error on the provider's say-so — state why the code can never be a transient or our own malformed request, and add it to this list deliberately");
    }

    [Theory]
    [InlineData("invalid_grant", true)]
    [InlineData("invalid_client", true)]
    [InlineData("INVALID_GRANT", false)]   // the codes are exact: RFC 6749 spells them lower-case, and nothing case-folds a verdict into existence
    [InlineData("http_500", false)]
    [InlineData("invalid_request", false)]
    [InlineData("temporarily_unavailable", false)]
    public void A_refusal_is_final_only_when_its_code_is_one_of_the_pinned_set(string error, bool final)
    {
        new OAuthExchangeException(error, "description", providerBody: null).IsFinalRefusal.ShouldBe(final);
    }

    [Fact]
    public void Naming_the_credential_keeps_what_the_provider_said_and_adds_which_credential_it_was_for()
    {
        var credentialId = Guid.NewGuid();
        var original = new OAuthExchangeException("invalid_grant", "The provided authorization grant is invalid", "{\"error\":\"invalid_grant\"}");

        var named = original.ForCredential(credentialId);

        named.Error.ShouldBe("invalid_grant");
        named.Description.ShouldBe("The provided authorization grant is invalid");
        named.ProviderBody.ShouldBe("{\"error\":\"invalid_grant\"}");
        named.Message.ShouldBe($"{original.Message} (credential {credentialId})");
        named.InnerException.ShouldBeSameAs(original);
        named.IsFinalRefusal.ShouldBeTrue();
        named.Code.ShouldBe(FailureCodes.OAuthExchangeFailed);
        named.Kind.ShouldBe(original.Kind);
    }

    [Fact]
    public void The_text_never_carries_more_than_the_provider_and_the_credential_id()
    {
        var named = new OAuthExchangeException("invalid_client", "Client authentication failed", providerBody: "client_secret=hunter2").ForCredential(Guid.NewGuid());

        named.Message.ShouldNotContain("hunter2", customMessage: "the provider's body stays on the exception for diagnostics and is never part of its message");
    }
}

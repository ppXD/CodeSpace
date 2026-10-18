using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.Credentials.Remedy;

/// <summary>
/// The words a person reads. Each code says what is wrong in plain language, names the credential and the repository when they are
/// known, leaves out what is not, and never carries a token or a status table.
/// </summary>
[Trait("Category", "Unit")]
public class RemedyCopyTests
{
    private static RemedyFacts Facts() => new() { Provider = "GitLab", CredentialName = "GitLab · NACHO.L", Repositories = "acme/api", RepositoryCount = 1, RequestedOrigin = "http://gitlab.example.com", RedirectsTo = "https://gitlab.example.com", ProviderStatus = "401", Lookup = StandingLookupCauses.AccessRefused };

    [Theory]
    [InlineData(FailureCodes.CredentialDisconnected, "The credential this run used was disconnected", "“GitLab · NACHO.L” was disconnected, so acme/api cannot authenticate with GitLab.")]
    [InlineData(FailureCodes.CredentialNeedsReconnect, "GitLab no longer accepts the credential this run used", "refused to renew the sign-in behind “GitLab · NACHO.L”")]
    [InlineData(FailureCodes.ProviderUnauthorized, "GitLab rejected the credential for this repository", "GitLab rejected “GitLab · NACHO.L” when it was used for acme/api (HTTP 401).")]
    [InlineData(FailureCodes.GitCredentialRejected, "GitLab refused the push", "GitLab refused “GitLab · NACHO.L” when its branch was pushed to acme/api, so nothing was published.")]
    [InlineData(FailureCodes.ProviderBaseUrlRedirects, "The provider's address redirects somewhere else", "Requests to http://gitlab.example.com are redirected to https://gitlab.example.com.")]
    [InlineData(FailureCodes.OAuthExchangeFailed, "Renewing the sign-in this run used did not work", "GitLab did not renew the sign-in behind “GitLab · NACHO.L” when acme/api needed it")]
    [InlineData(WorkflowRunRequestRejectionReasons.CredentialRejected, "GitLab refused the credential this trigger uses", "The author is not in question — the credential is.")]
    [InlineData(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, "GitLab would not say who wrote the pull request", "lacks the read_api scope or a role on the project")]
    public void Each_code_says_what_is_wrong_and_names_what_it_is_about(string code, string title, string cause)
    {
        RemedyCopy.Title(code, Facts()).ShouldBe(title);
        RemedyCopy.Cause(code, Facts()).ShouldContain(cause);
    }

    [Fact]
    public void A_redirect_says_what_actually_works_for_the_repair_the_server_will_accept()
    {
        var edit = RemedyCopy.Cause(FailureCodes.ProviderBaseUrlRedirects, Facts() with { RedirectFix = RedirectRepair.EditAddress });
        var disconnect = RemedyCopy.Cause(FailureCodes.ProviderBaseUrlRedirects, Facts() with { RedirectFix = RedirectRepair.DisconnectFirst, ConnectedCredentials = 2 });
        var hostSide = RemedyCopy.Cause(FailureCodes.ProviderBaseUrlRedirects, Facts() with { RedirectFix = RedirectRepair.HostSide, RedirectsTo = "http://gitlab.example.com" });

        edit.ShouldContain("Change the provider's address to https://gitlab.example.com");
        disconnect.ShouldNotContain("Change the provider's address to", Case.Insensitive);
        disconnect.ShouldContain("2 credentials are connected");
        disconnect.ShouldContain("Disconnect them first");
        disconnect.ShouldContain("add https://gitlab.example.com as a provider of its own");
        hostSide.ShouldNotContain("redirected to http://gitlab.example.com", Case.Insensitive, "the same address on both sides is not a redirect to somewhere else");
        hostSide.ShouldContain("remove the redirect on the host");
        hostSide.ShouldNotContain("Change the provider's address", Case.Insensitive);
    }

    [Fact]
    public void A_provider_that_did_not_answer_a_renewal_does_not_blame_the_credential()
    {
        var outage = Facts() with { OAuthError = "http_503" };
        var cause = RemedyCopy.Cause(FailureCodes.OAuthExchangeFailed, outage);

        cause.ShouldContain("(it answered http_503)");
        cause.ShouldContain("says nothing about the credential");
        cause.ShouldNotContain("refused to refresh", Case.Insensitive);
        cause.ShouldNotContain("Reconnect it and nothing", Case.Insensitive, "a healthy credential is not sent to be repaired; reconnecting is what to do if it keeps failing");
        RemedyCopy.Cause(FailureCodes.OAuthExchangeFailed, Facts()).ShouldNotContain("(it answered", Case.Insensitive, "an answer that was not recorded is not invented");
    }

    [Theory]
    // what the lookup found → what the sentence says is wrong, and whether it sends anyone to repair the credential
    [InlineData(StandingLookupCauses.AccessRefused, "lacks the read_api scope or a role on the project", true)]
    [InlineData(StandingLookupCauses.NoAnswer, "could not be asked", false)]
    [InlineData(StandingLookupCauses.NoCredential, "no credential is linked", false)]
    [InlineData(null, "can also be a timeout or an error", true)]   // a row written before the cause was kept: both are possible, so both are said
    public void A_standing_lookup_that_failed_says_what_failed_not_always_that_the_credential_needs_access(string? lookup, string says, bool mentionsReconnecting)
    {
        var facts = Facts() with { Lookup = lookup, ProviderStatus = "503" };
        var title = RemedyCopy.Title(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, facts);
        var cause = RemedyCopy.Cause(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, facts);

        (title + " " + cause).ShouldContain(says, Case.Insensitive);
        (cause.Contains("reconnect", StringComparison.OrdinalIgnoreCase) || cause.Contains("replace", StringComparison.OrdinalIgnoreCase)).ShouldBe(mentionsReconnecting, cause);
        cause.ShouldContain("Anyone is not the fix", customMessage: "loosening the trigger never fixes a lookup that failed");
    }

    [Fact]
    public void A_lookup_that_got_no_answer_says_what_the_provider_answered_when_it_answered_at_all()
    {
        RemedyCopy.Cause(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, Facts() with { Lookup = StandingLookupCauses.NoAnswer, ProviderStatus = "429" }).ShouldContain("(HTTP 429)");
        RemedyCopy.Cause(WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, Facts() with { Lookup = StandingLookupCauses.NoAnswer, ProviderStatus = null }).ShouldNotContain("HTTP");
    }

    [Fact]
    public void A_retired_credential_says_it_cannot_be_reconnected()
    {
        var facts = Facts() with { CredentialRetired = true };

        RemedyCopy.Title(FailureCodes.CredentialDisconnected, facts).ShouldBe("The credential this run used was retired");
        RemedyCopy.Cause(FailureCodes.CredentialDisconnected, facts).ShouldContain("cannot be reconnected");
    }

    [Fact]
    public void A_pasted_token_says_token_and_a_sign_in_says_sign_in()
    {
        RemedyCopy.Cause(FailureCodes.CredentialNeedsReconnect, Facts() with { CredentialIsStaticToken = true }).ShouldContain("rejected the token behind");
        RemedyCopy.Cause(FailureCodes.CredentialNeedsReconnect, Facts()).ShouldContain("renew the sign-in behind");
    }

    [Fact]
    public void What_is_not_known_is_left_out_not_invented()
    {
        var bare = new RemedyFacts();

        RemedyCopy.Cause(FailureCodes.CredentialDisconnected, bare).ShouldBe("The credential was disconnected, so the repository cannot authenticate with the provider. Reconnect it to put it back, or link the repository to a credential that works.");
        RemedyCopy.Title(FailureCodes.GitCredentialRejected, bare).ShouldBe("The provider refused the push");
    }

    [Fact]
    public void A_fixed_connection_says_so_and_what_to_do_next()
    {
        var resolved = Facts() with { Resolved = true };

        RemedyCopy.Title(FailureCodes.CredentialDisconnected, resolved).ShouldBe("This connection works again");
        RemedyCopy.Cause(FailureCodes.CredentialDisconnected, resolved).ShouldBe("“GitLab · NACHO.L” works again, so acme/api can authenticate. Run it again to pick up where this stopped.");
        RemedyCopy.Cause(FailureCodes.ProviderBaseUrlRedirects, resolved).ShouldBe("The provider's address no longer redirects. Run it again to pick up where this stopped.");
        RemedyCopy.Cause(WorkflowRunRequestRejectionReasons.CredentialRejected, resolved).ShouldContain("the next pull request event on acme/api starts its run");
    }

    [Fact]
    public void Nothing_a_remedy_says_carries_a_token()
    {
        foreach (var code in FailureCodes.ProviderConnection)
        {
            RemedyCopy.Cause(code, Facts()).ShouldNotContain("glpat", Case.Insensitive);
            RemedyCopy.Title(code, Facts()).ShouldNotBeNullOrWhiteSpace();
        }
    }
}

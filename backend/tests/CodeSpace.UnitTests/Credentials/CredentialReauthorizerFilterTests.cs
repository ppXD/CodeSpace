using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Credentials;
using Shouldly;
using static CodeSpace.Core.Services.Credentials.CredentialReauthorizer;

namespace CodeSpace.UnitTests.Credentials;

/// <summary>
/// 🟢 High fidelity — the two pure decisions a re-authorization makes before it touches anything: whether the account that
/// signed in is the account the credential was connected as, and which of the tokens it replaced the provider is asked to
/// end. Everything around them (the lock, the write, the revival) is exercised against real Postgres in
/// <c>CredentialReauthorizationFlowTests</c>.
/// </summary>
[Trait("Category", "Unit")]
public class CredentialReauthorizerFilterTests
{
    [Theory]
    [InlineData("1001", "nacho", "1001", "NACHO", AccountMatch.Same)]               // ids agree; the handle's case is no evidence
    [InlineData("1001", "nacho", "1002", "nacho", AccountMatch.Different)]          // the provider's id is authoritative: a handle can be reused by someone else
    [InlineData("1001", "nacho", "1001", "renamed", AccountMatch.Same)]             // the same account after a rename
    [InlineData("", "nacho", "1002", "NACHO", AccountMatch.Same)]                   // no id on record: the handle decides
    [InlineData("1001", "nacho", null, "someone-else", AccountMatch.Different)]     // the provider gave no id: the handle decides
    [InlineData("1001", null, "1001", null, AccountMatch.Same)]
    [InlineData(null, null, "1001", "nacho", AccountMatch.Unknown)]                 // nothing on record to compare
    [InlineData("1001", "nacho", null, null, AccountMatch.Unknown)]                 // the provider said nothing about who signed in
    [InlineData("  ", "  ", "1001", "nacho", AccountMatch.Unknown)]                 // blank is not a name
    public void The_account_that_signed_in_is_compared_with_the_account_on_record(string? knownId, string? knownName, string? presentedId, string? presentedName, AccountMatch expected)
    {
        Compare(knownId, knownName, presentedId, presentedName).ShouldBe(expected);
    }

    [Fact]
    public void The_tokens_a_re_authorization_replaced_are_the_ones_to_end()
    {
        var replaced = new OAuthPayload { AccessToken = "at-old", RefreshToken = "rt-old", ExpiresAt = DateTimeOffset.UtcNow.AddHours(-1) };
        var fresh = new OAuthPayload { AccessToken = "at-new", RefreshToken = "rt-new", ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) };

        ReplacedPairToRevoke(replaced, fresh).ShouldBeSameAs(replaced);
    }

    [Theory]
    [InlineData("at-same", "rt-old", "at-same", "rt-new")]   // the provider reused the access token for the second sign-in
    [InlineData("at-old", "rt-same", "at-new", "rt-same")]   // ... or the refresh token: GitLab through 15.4 hands the same grant to a second authorization
    [InlineData("at-same", "rt-same", "at-same", "rt-same")] // ... or both
    public void A_token_the_new_authorization_shares_is_never_revoked_because_revoking_it_would_end_the_new_one(string oldAccess, string oldRefresh, string newAccess, string newRefresh)
    {
        var replaced = new OAuthPayload { AccessToken = oldAccess, RefreshToken = oldRefresh };
        var fresh = new OAuthPayload { AccessToken = newAccess, RefreshToken = newRefresh };

        ReplacedPairToRevoke(replaced, fresh).ShouldBeNull("the row's old pair and its new one are one grant, so there is no old pair left to end");
    }

    [Fact]
    public void Two_tokens_without_refresh_tokens_are_compared_by_their_access_tokens_alone()
    {
        // A GitHub OAuth App token has no refresh token, and "no refresh token" is not a token both sides share.
        var replaced = new OAuthPayload { AccessToken = "gho-old" };

        ReplacedPairToRevoke(replaced, new OAuthPayload { AccessToken = "gho-new" }).ShouldBeSameAs(replaced);
        ReplacedPairToRevoke(replaced, new OAuthPayload { AccessToken = "gho-old" }).ShouldBeNull();
    }

    [Fact]
    public void A_replaced_personal_access_token_has_nothing_to_end_at_the_provider()
    {
        ReplacedPairToRevoke(new PatPayload { Token = "glpat-old" }, new PatPayload { Token = "glpat-new" }).ShouldBeNull("a token the provider issued as a PAT is not one CodeSpace can revoke");
        ReplacedPairToRevoke(new PatPayload { Token = "glpat-old" }, new OAuthPayload { AccessToken = "at-new", RefreshToken = "rt-new" }).ShouldBeNull();
    }

    [Fact]
    public void A_blank_row_has_nothing_to_end()
    {
        ReplacedPairToRevoke(null, new OAuthPayload { AccessToken = "at-new", RefreshToken = "rt-new" }).ShouldBeNull("a Disconnect already ended the tokens it blanked");
    }

    [Fact]
    public void An_oauth_pair_replaced_by_a_pasted_token_is_ended()
    {
        var replaced = new OAuthPayload { AccessToken = "at-old", RefreshToken = "rt-old" };

        ReplacedPairToRevoke(replaced, new PatPayload { Token = "glpat-new" }).ShouldBeSameAs(replaced);
    }
}

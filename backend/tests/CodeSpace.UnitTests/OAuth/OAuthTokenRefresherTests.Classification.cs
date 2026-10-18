using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.OAuth;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Shouldly;

namespace CodeSpace.UnitTests.OAuth;

/// <summary>
/// What the refresher does with a refusal that stands, and with an access token the provider refused after it was handed out.
///
/// <para>A refusal that names the grant or the client, with the row unchanged, is the provider's verdict: the credential is
/// marked, over the row the verdict was judged against, and the caller is told to reconnect it — naming it. Any other
/// refusal says nothing about the credential. After a 401 the refresher adopts what another caller already stored, or
/// rotates once; a token it rotated moments ago is not rotated again.</para>
/// </summary>
public partial class OAuthTokenRefresherTests
{
    [Fact]
    public async Task A_final_refusal_marks_the_credential_over_the_row_it_judged_and_names_it()
    {
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var health = new RecordingHealthWriter();

        var verdict = await Should.ThrowAsync<CredentialNeedsReconnectException>(() => RunAsync(stale, ctx, row, RecordingOAuthClient.Refusing(), healthWriter: health));

        verdict.CredentialId.ShouldBe(ctx.Credential.Id);
        verdict.DisplayName.ShouldBe("cred");
        verdict.Reason.ShouldBe("invalid_grant");
        verdict.Message.ShouldContain(ctx.Credential.Id.ToString());
        verdict.Code.ShouldBe(FailureCodes.CredentialNeedsReconnect);

        var recorded = health.Rejections.ShouldHaveSingleItem();

        recorded.CredentialId.ShouldBe(ctx.Credential.Id);
        recorded.LastError.ShouldBe("needs reconnect: invalid_grant");
        recorded.ExpectedPayload.ShouldBe(Serializer.Serialize(stale), "the verdict is written against the row the refusal was judged against, so a reconnect that replaced it meanwhile is never overwritten");
    }

    [Theory]
    [InlineData("invalid_grant", true)]
    [InlineData("invalid_client", true)]
    [InlineData("unauthorized_client", true)]
    [InlineData("bad_refresh_token", true)]
    [InlineData("incorrect_client_credentials", true)]
    [InlineData("http_503", false)]
    [InlineData("temporarily_unavailable", false)]
    [InlineData("server_error", false)]
    [InlineData("invalid_request", false)]
    [InlineData("malformed_response", false)]
    [InlineData("unknown_error", false)]
    public async Task Only_a_refusal_of_the_grant_or_the_client_is_a_verdict(string oauthError, bool isVerdict)
    {
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var health = new RecordingHealthWriter();

        var failure = await Should.ThrowAsync<Exception>(() => RunAsync(stale, ctx, new StoredRow(stale), RecordingOAuthClient.Refusing(error: oauthError), healthWriter: health));

        if (isVerdict)
        {
            failure.ShouldBeOfType<CredentialNeedsReconnectException>();
            health.Rejections.ShouldHaveSingleItem().LastError.ShouldBe($"needs reconnect: {oauthError}");
            return;
        }

        var exchange = failure.ShouldBeOfType<OAuthExchangeException>($"'{oauthError}' says nothing about the credential, so it stays the provider's failure");
        exchange.Error.ShouldBe(oauthError);
        exchange.Message.ShouldContain(ctx.Credential.Id.ToString(), customMessage: "the provider's answer names no credential; the failure must, or an operator cannot tell which row it was for");
        health.Rejections.ShouldBeEmpty();
    }

    [Fact]
    public async Task A_verdict_that_cannot_be_recorded_still_reaches_the_caller()
    {
        var ctx = BuildContext();
        var stale = ExpiredPair();

        await Should.ThrowAsync<CredentialNeedsReconnectException>(() => RunAsync(stale, ctx, new StoredRow(stale), RecordingOAuthClient.Refusing(), healthWriter: new RecordingHealthWriter { Fails = true }));
    }

    [Fact]
    public async Task A_refusal_the_row_moved_past_is_not_a_verdict()
    {
        // The lock session was lost and the pod that took over stored a new pair: the refusal was about the old one.
        var ctx = BuildContext();
        var stale = ExpiredPair();
        var row = new StoredRow(stale);
        var health = new RecordingHealthWriter();
        var provider = RecordingOAuthClient.Refusing(onRefusing: () => row.Store(new OAuthPayload { AccessToken = "rotated", RefreshToken = "r-1", ExpiresAt = Now.AddHours(2) }));

        var (result, _, _) = await RunAsync(stale, ctx, row, provider, healthWriter: health);

        result.AccessToken.ShouldBe("rotated");
        health.Rejections.ShouldBeEmpty();
    }

    // ─── After a 401 ───────────────────────────────────────────────────────────

    private static OAuthPayload LivePair(string accessToken = "a", string? refreshToken = "r-0") => new() { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = Now.AddHours(1) };

    private static Task<OAuthPayload?> RecoverAsync(OAuthPayload entity, string rejected, ProviderContext ctx, StoredRow row, RecordingOAuthClient provider, ICredentialHealthWriter? health = null) =>
        BuildRefresher(entity, ctx, row, provider, healthWriter: health).Refresher.RecoverAfterRejectionAsync(ctx, rejected, CancellationToken.None);

    [Fact]
    public async Task A_token_another_caller_stored_is_adopted_instead_of_rotating()
    {
        var ctx = BuildContext();
        var entity = LivePair("old", "r-consumed");
        var row = new StoredRow(LivePair("rotated", "r-rotated"));
        var provider = new RecordingOAuthClient("unused", null, null);

        var recovered = await RecoverAsync(entity, "old", ctx, row, provider);

        recovered.ShouldNotBeNull().AccessToken.ShouldBe("rotated");
        provider.RefreshCalls.ShouldBe(0, "the provider retired the token because the pair was rotated and stored; rotating again would consume the pair that was stored");
        ResolveEntity(ctx).AccessToken.ShouldBe("rotated", "the caller's entity carries it, so later reads in the scope are not refused the same way");
    }

    [Fact]
    public async Task The_token_that_was_refused_is_rotated_once_and_the_new_pair_stored()
    {
        var ctx = BuildContext();
        var pair = LivePair("a", "r-0");
        var row = new StoredRow(pair);
        var provider = new RecordingOAuthClient("b", "r-1", Now.AddHours(2));

        var recovered = await RecoverAsync(pair, "a", ctx, row, provider);

        recovered.ShouldNotBeNull().AccessToken.ShouldBe("b");
        provider.RefreshCalls.ShouldBe(1);
        row.Read().RefreshToken.ShouldBe("r-1", "the pair the provider issued is the credential now");
    }

    [Fact]
    public async Task A_token_rotated_moments_ago_that_is_refused_too_is_not_rotated_again()
    {
        var ctx = BuildContext();
        var row = new StoredRow(LivePair("a", "r-0"));
        var provider = new RecordingOAuthClient("b", "r-1", Now.AddHours(2));

        await RecoverAsync(LivePair("a", "r-0"), "a", ctx, row, provider);

        var again = await RecoverAsync(row.Read(), "b", ctx, row, provider);

        again.ShouldBeNull("a token issued a moment ago that is refused is not aged out: the 401 has another cause, and rotating again would spend a refresh token on it");
        provider.RefreshCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_credential_with_no_refresh_token_has_nothing_to_rotate_with()
    {
        var ctx = BuildContext();
        var pair = LivePair("a", refreshToken: null);
        var provider = new RecordingOAuthClient("b", "r-1", Now.AddHours(2));

        (await RecoverAsync(pair, "a", ctx, new StoredRow(pair), provider)).ShouldBeNull();

        provider.RefreshCalls.ShouldBe(0);
    }

    [Fact]
    public async Task A_stored_token_that_is_itself_due_is_refreshed_not_handed_back_to_be_refused_again()
    {
        var ctx = BuildContext();
        var row = new StoredRow(ExpiredPair("stored", "r-9"));
        var provider = new RecordingOAuthClient("b", "r-10", Now.AddHours(2));

        var recovered = await RecoverAsync(LivePair("old", "r-consumed"), "old", ctx, row, provider);

        recovered.ShouldNotBeNull().AccessToken.ShouldBe("b");
        provider.RefreshCalls.ShouldBe(1);
    }

    [Fact]
    public async Task A_rotation_the_provider_refuses_for_good_after_a_401_is_a_verdict_too()
    {
        var ctx = BuildContext();
        var pair = LivePair("a", "r-0");
        var health = new RecordingHealthWriter();

        await Should.ThrowAsync<CredentialNeedsReconnectException>(() => RecoverAsync(pair, "a", ctx, new StoredRow(pair), RecordingOAuthClient.Refusing(), health));

        health.Rejections.ShouldHaveSingleItem().LastError.ShouldBe("needs reconnect: invalid_grant");
    }

    [Fact]
    public async Task A_credential_revoked_meanwhile_is_not_recovered()
    {
        var ctx = BuildContext();
        var pair = LivePair();
        var row = new StoredRow(pair);
        row.Revoke();
        var provider = new RecordingOAuthClient("b", "r-1", Now.AddHours(2));

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => RecoverAsync(pair, "a", ctx, row, provider));

        failure.Message.ShouldContain("was revoked");
        provider.RefreshCalls.ShouldBe(0);
    }
}

using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Shouldly;

namespace CodeSpace.IntegrationTests.Infrastructure;

/// <summary>
/// 🔵 Fixture-only fidelity: these tests pin what <see cref="GitLabDoorkeeperStub"/> does, so that a test built on it that
/// goes red is red because the product misbehaved and not because the stub drifted from the GitLab it stands for. They do
/// not count as coverage of any production class.
///
/// <para>The behaviours pinned are the ones the provider-credential tests lean on: a refresh token is single use and the
/// refresh kills the old access token with it; GitLab revokes no wider family on a reuse; the REST API honours an OAuth
/// token only as <c>Authorization: Bearer</c>; git smart-HTTP wants an OAuth token under the login <c>oauth2</c>.</para>
/// </summary>
[Trait("Category", "Integration")]
public sealed class GitLabDoorkeeperStubTests : IDisposable
{
    private readonly GitLabDoorkeeperStub _gitlab = new();
    private readonly HttpClient _http = new();

    public void Dispose()
    {
        _http.Dispose();
        _gitlab.Dispose();
    }

    [Fact]
    public async Task A_refresh_token_is_single_use_and_the_refresh_ends_the_old_access_token_with_it()
    {
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        var first = await RefreshAsync("rt-0").ConfigureAwait(false);
        var second = await RefreshAsync("rt-0").ConfigureAwait(false);

        first.Status.ShouldBe(HttpStatusCode.OK);
        first.Json.GetProperty("refresh_token").GetString().ShouldBe("rt-2");
        second.Status.ShouldBe(HttpStatusCode.BadRequest);
        second.Json.GetProperty("error").GetString().ShouldBe("invalid_grant");
        _gitlab.Count(StubEvent.Rotated).ShouldBe(1);
        _gitlab.Count(StubEvent.Refused).ShouldBe(1);
        _gitlab.RefusedRefreshTokens().ShouldBe(new[] { "rt-0" });
        (await UserStatusAsync("at-0").ConfigureAwait(false)).ShouldBe(HttpStatusCode.Unauthorized, "the access token lived on a row the refresh revoked, though its expires_in has not elapsed");
        (await UserStatusAsync("at-2").ConfigureAwait(false)).ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refused_reuse_leaves_the_pair_the_winner_was_issued_alive()
    {
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        await RefreshAsync("rt-0").ConfigureAwait(false);
        await RefreshAsync("rt-0").ConfigureAwait(false);

        _gitlab.IsRefreshTokenLive("rt-2").ShouldBeTrue("GitLab revokes no wider family on a reuse (gitlab-org/gitlab#364111)");
        _gitlab.LiveRefreshTokenOf("nacho").ShouldBe("rt-2");
        (await RefreshAsync("rt-2").ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_refresh_is_committed_before_it_is_answered()
    {
        _gitlab.RotateReplyDelay = TimeSpan.FromMilliseconds(800);
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        var pending = RefreshAsync("rt-0");
        await WaitForAsync(() => _gitlab.Count(StubEvent.Rotated) == 1, "the stub never rotated the pair").ConfigureAwait(false);

        pending.IsCompleted.ShouldBeFalse("the answer is still being held back");
        _gitlab.IsRefreshTokenLive("rt-0").ShouldBeFalse("a caller that gives up on the answer has still consumed its refresh token");
        (await pending.ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("app1", "wrong", "invalid_client")]
    [InlineData("nobody", "secret1", "invalid_client")]
    [InlineData("app2", "secret2", "invalid_grant")]
    public async Task A_refresh_by_the_wrong_client_is_refused_and_consumes_nothing(string clientId, string clientSecret, string expectedError)
    {
        _gitlab.AddApplication("app2", "secret2");
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        var refused = await RefreshAsync("rt-0", clientId, clientSecret).ConfigureAwait(false);

        refused.Json.GetProperty("error").GetString().ShouldBe(expectedError);
        _gitlab.IsRefreshTokenLive("rt-0").ShouldBeTrue("a refusal for the client must not spend the user's refresh token");
    }

    [Theory]
    [InlineData("at-0")]
    [InlineData("rt-0")]
    public async Task Revoking_either_token_ends_the_row_and_revoking_an_unknown_token_is_still_ok(string revoked)
    {
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        (await PostAsync("/oauth/revoke", new() { ["client_id"] = "app1", ["client_secret"] = "secret1", ["token"] = revoked }).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.OK);
        (await PostAsync("/oauth/revoke", new() { ["client_id"] = "app1", ["client_secret"] = "secret1", ["token"] = "never-issued" }).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.OK);

        _gitlab.IsAccessTokenLive("at-0").ShouldBeFalse();
        _gitlab.IsRefreshTokenLive("rt-0").ShouldBeFalse();
        _gitlab.Count(StubEvent.Revoked).ShouldBe(2);
    }

    [Fact]
    public async Task An_authorization_code_issues_the_user_a_first_pair_and_leaves_their_earlier_ones_alone()
    {
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        var issued = await PostAsync("/oauth/token", new() { ["client_id"] = "app1", ["client_secret"] = "secret1", ["grant_type"] = "authorization_code", ["code"] = "authz:nacho" }).ConfigureAwait(false);

        issued.Status.ShouldBe(HttpStatusCode.OK);
        issued.Json.GetProperty("access_token").GetString().ShouldBe("at-2");
        _gitlab.IsAccessTokenLive("at-0").ShouldBeTrue("a second connect does not end the first");
        _gitlab.Count(StubEvent.Authorized).ShouldBe(1);
    }

    [Fact]
    public async Task A_token_past_its_expires_in_is_refused_by_the_api()
    {
        _gitlab.ExpiresInSeconds = 0;
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        (await UserStatusAsync("at-0").ConfigureAwait(false)).ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_api_honours_an_oauth_token_only_as_a_bearer_and_answers_the_members_lookup_for_the_user_asked_about()
    {
        _gitlab.MemberAccessLevel = 40;
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        var asBearer = await GetAsync("/api/v4/projects/123/members/all/51", ("Authorization", "Bearer at-0")).ConfigureAwait(false);
        var asPrivateToken = await GetAsync("/api/v4/projects/123/members/all/51", ("PRIVATE-TOKEN", "at-0")).ConfigureAwait(false);
        var withBoth = await GetAsync("/api/v4/projects/123/members/all/51", ("Authorization", "Bearer at-0"), ("PRIVATE-TOKEN", "at-0")).ConfigureAwait(false);

        asBearer.Status.ShouldBe(HttpStatusCode.OK);
        asBearer.Json.GetProperty("id").GetInt64().ShouldBe(51);
        asBearer.Json.GetProperty("access_level").GetInt32().ShouldBe(40);
        asPrivateToken.Status.ShouldBe(HttpStatusCode.Unauthorized, "PRIVATE-TOKEN is looked up as a personal access token, which an OAuth access token is not");
        withBoth.Status.ShouldBe(HttpStatusCode.OK, "what the member-standing lookup sends");
        _gitlab.Count(StubEvent.Api200).ShouldBe(2);
        _gitlab.Count(StubEvent.Api401).ShouldBe(1);
    }

    [Fact]
    public async Task Every_user_has_an_id_of_their_own_that_the_user_endpoint_reports_for_whoever_holds_the_token()
    {
        _gitlab.Seed("app1", "nacho", "at-nacho", "rt-nacho");
        _gitlab.Seed("app1", "maya", "at-maya", "rt-maya");
        _gitlab.SeedPersonalAccessToken("maya", "glpat-maya");

        var nacho = await GetAsync("/api/v4/user", ("Authorization", "Bearer at-nacho")).ConfigureAwait(false);
        var maya = await GetAsync("/api/v4/user", ("Authorization", "Bearer at-maya")).ConfigureAwait(false);
        var mayaByToken = await GetAsync("/api/v4/user", ("PRIVATE-TOKEN", "glpat-maya")).ConfigureAwait(false);

        nacho.Json.GetProperty("id").GetInt64().ShouldBe(_gitlab.UserIdOf("nacho"));
        maya.Json.GetProperty("id").GetInt64().ShouldBe(_gitlab.UserIdOf("maya"));
        mayaByToken.Json.GetProperty("id").GetInt64().ShouldBe(_gitlab.UserIdOf("maya"), "one account, one id, whichever kind of token asked");
        _gitlab.UserIdOf("nacho").ShouldNotBe(_gitlab.UserIdOf("maya"), "a re-authorization is refused for another account by its id, so two users must not share one");
        maya.Json.GetProperty("username").GetString().ShouldBe("maya");
    }

    [Fact]
    public async Task A_seeded_personal_access_token_is_an_access_token_that_never_expires_and_reports_its_own_scopes()
    {
        _gitlab.SeedPersonalAccessToken("nacho", "glpat-live");

        var asPrivateToken = await GetAsync("/api/v4/user", ("PRIVATE-TOKEN", "glpat-live")).ConfigureAwait(false);
        var asBearer = await GetAsync("/api/v4/user", ("Authorization", "Bearer glpat-live")).ConfigureAwait(false);
        var unknown = await GetAsync("/api/v4/user", ("PRIVATE-TOKEN", "glpat-never-heard-of")).ConfigureAwait(false);
        var self = await GetAsync("/api/v4/personal_access_tokens/self", ("PRIVATE-TOKEN", "glpat-live")).ConfigureAwait(false);

        asPrivateToken.Status.ShouldBe(HttpStatusCode.OK);
        asBearer.Status.ShouldBe(HttpStatusCode.Unauthorized, "an access token is not an OAuth token: GitLab looks a Bearer up as OAuth alone");
        unknown.Status.ShouldBe(HttpStatusCode.Unauthorized);
        self.Json.GetProperty("scopes").EnumerateArray().Select(e => e.GetString()).ShouldBe(GitLabDoorkeeperStub.PersonalAccessTokenScopes.Split(','));
        self.Json.GetProperty("expires_at").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Theory]
    [InlineData("oauth2", true, HttpStatusCode.OK)]
    [InlineData("nacho", true, HttpStatusCode.Unauthorized)]
    [InlineData("oauth2", false, HttpStatusCode.Unauthorized)]
    public async Task Git_takes_an_oauth_token_as_basic_only_under_the_login_oauth2_and_only_while_it_is_live(string login, bool live, HttpStatusCode expected)
    {
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");
        var token = live ? "at-0" : "at-dead";

        var advertisement = await GetAsync("/acme/api.git/info/refs?service=git-upload-pack", ("Authorization", "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{login}:{token}")))).ConfigureAwait(false);

        advertisement.Status.ShouldBe(expected);
        if (expected == HttpStatusCode.Unauthorized) advertisement.Challenge.ShouldBe("Basic realm=\"GitLab\"", "the challenge is what makes git ask again with credentials");
    }

    [Fact]
    public async Task Git_takes_a_bearer_token_and_challenges_a_request_with_no_credentials()
    {
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        (await GetAsync("/acme/api.git/info/refs?service=git-receive-pack", ("Authorization", "Bearer at-0")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.OK);
        (await GetAsync("/acme/api.git/info/refs?service=git-upload-pack").ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.Unauthorized);
        _gitlab.Count(StubEvent.Git200).ShouldBe(1);
        _gitlab.Count(StubEvent.Git401).ShouldBe(1);
    }

    [Fact]
    public async Task An_access_token_travels_as_private_token_describes_itself_and_stops_when_it_is_revoked_or_expires()
    {
        _gitlab.AddAccessToken("glpat-live", "nacho", DateTimeOffset.UtcNow.AddDays(3));
        _gitlab.AddAccessToken("glpat-forever", "nacho");
        _gitlab.AddAccessToken("glpat-lapsed", "nacho", DateTimeOffset.UtcNow.AddMinutes(-1));
        _gitlab.AddAccessToken("glpat-revoked", "nacho");
        _gitlab.RevokeAccessToken("glpat-revoked");

        (await GetAsync("/api/v4/user", ("PRIVATE-TOKEN", "glpat-live")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.OK);
        (await GetAsync("/api/v4/user", ("PRIVATE-TOKEN", "glpat-lapsed")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.Unauthorized, "GitLab refuses a token that reached its expiry");
        (await GetAsync("/api/v4/user", ("PRIVATE-TOKEN", "glpat-revoked")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.Unauthorized);
        (await GetAsync("/api/v4/user", ("PRIVATE-TOKEN", "glpat-unknown")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.Unauthorized, "a PRIVATE-TOKEN the stub was not given is looked up as an OAuth token, which fails as before");
        (await GetAsync("/api/v4/user", ("Authorization", "Bearer glpat-live")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.Unauthorized, "an access token is not an OAuth token: GitLab looks a Bearer up as OAuth alone");

        var self = await GetAsync("/api/v4/personal_access_tokens/self", ("PRIVATE-TOKEN", "glpat-live")).ConfigureAwait(false);

        self.Json.GetProperty("expires_at").GetString().ShouldBe(DateTimeOffset.UtcNow.AddDays(3).UtcDateTime.ToString("yyyy-MM-dd"), "a date, as GitLab spells it");
        self.Json.GetProperty("active").GetBoolean().ShouldBeTrue();
        (await GetAsync("/api/v4/personal_access_tokens/self", ("PRIVATE-TOKEN", "glpat-forever")).ConfigureAwait(false)).Json.GetProperty("expires_at").ValueKind.ShouldBe(JsonValueKind.Null);
        _gitlab.Count(StubEvent.TokenApi200).ShouldBe(3);
        _gitlab.Count(StubEvent.TokenApi401).ShouldBe(2, "the lapsed and the revoked token");
    }

    [Fact]
    public async Task An_oauth_token_cannot_describe_itself_as_an_access_token_does()
    {
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");

        (await GetAsync("/api/v4/personal_access_tokens/self", ("Authorization", "Bearer at-0")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_scripted_members_failure_answers_the_next_lookups_for_either_kind_of_token_and_then_the_role_again()
    {
        _gitlab.Seed("app1", "nacho", "at-0", "rt-0");
        _gitlab.AddAccessToken("glpat-live", "nacho");
        _gitlab.FailMembers(403, times: 2);

        (await GetAsync("/api/v4/projects/42/members/all/7", ("Authorization", "Bearer at-0")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync("/api/v4/projects/42/members/all/7", ("PRIVATE-TOKEN", "glpat-live")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.Forbidden);
        (await GetAsync("/api/v4/projects/42/members/all/7", ("Authorization", "Bearer at-0")).ConfigureAwait(false)).Json.GetProperty("access_level").GetInt32().ShouldBe(30);
        (await GetAsync("/api/v4/user", ("Authorization", "Bearer at-0")).ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.OK, "only the members lookup is scripted");
        _gitlab.Count(StubEvent.MembersFailed).ShouldBe(2);
    }

    [Fact]
    public async Task A_route_the_stub_does_not_know_is_counted_rather_than_silently_answered()
    {
        (await GetAsync("/somewhere/else").ConfigureAwait(false)).Status.ShouldBe(HttpStatusCode.NotImplemented);

        _gitlab.Count(StubEvent.Unrouted).ShouldBe(1);
    }

    [Fact]
    public async Task Real_git_lists_the_remote_with_a_live_token_and_is_refused_with_a_dead_one()
    {
        _gitlab.Seed("app1", "nacho", "at-live", "rt-0");
        _gitlab.Seed("app1", "nacho", "at-dead", "rt-9");
        await PostAsync("/oauth/revoke", new() { ["client_id"] = "app1", ["client_secret"] = "secret1", ["token"] = "at-dead" }).ConfigureAwait(false);

        var live = await LsRemoteAsync("at-live").ConfigureAwait(false);
        var dead = await LsRemoteAsync("at-dead").ConfigureAwait(false);

        live.ExitCode.ShouldBe(0, $"git ls-remote with a live token: {live.Output}");
        dead.ExitCode.ShouldNotBe(0, "git ls-remote with a revoked token should have been refused");
        dead.Output.ShouldContain("Authentication failed");
        _gitlab.Count(StubEvent.Git200).ShouldBeGreaterThan(0);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private sealed record Answer(HttpStatusCode Status, JsonElement Json, string? Challenge);

    private Task<Answer> RefreshAsync(string refreshToken, string clientId = "app1", string clientSecret = "secret1") =>
        PostAsync("/oauth/token", new() { ["client_id"] = clientId, ["client_secret"] = clientSecret, ["grant_type"] = "refresh_token", ["refresh_token"] = refreshToken });

    private async Task<HttpStatusCode> UserStatusAsync(string accessToken) => (await GetAsync("/api/v4/user", ("Authorization", "Bearer " + accessToken)).ConfigureAwait(false)).Status;

    private async Task<Answer> PostAsync(string path, Dictionary<string, string> form)
    {
        using var response = await _http.PostAsync(_gitlab.BaseUrl + path, new FormUrlEncodedContent(form)).ConfigureAwait(false);

        return await ReadAsync(response).ConfigureAwait(false);
    }

    private async Task<Answer> GetAsync(string pathAndQuery, params (string Name, string Value)[] headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _gitlab.BaseUrl + pathAndQuery);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);

        using var response = await _http.SendAsync(request).ConfigureAwait(false);

        return await ReadAsync(response).ConfigureAwait(false);
    }

    private static async Task<Answer> ReadAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        var json = body.TrimStart().StartsWith('{') || body.TrimStart().StartsWith('[') ? JsonDocument.Parse(body).RootElement.Clone() : default;
        var challenge = response.Headers.WwwAuthenticate.FirstOrDefault()?.ToString();

        return new Answer(response.StatusCode, json, challenge);
    }

    /// <summary>Real git against the stub, with nothing from the developer's own git or credential configuration in play.</summary>
    private async Task<(int ExitCode, string Output)> LsRemoteAsync(string accessToken)
    {
        var home = Directory.CreateTempSubdirectory("stub-git-home-").FullName;

        try
        {
            var psi = new ProcessStartInfo("git") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var arg in new[] { "ls-remote", $"http://oauth2:{accessToken}@{_gitlab.BaseUrl["http://".Length..]}/acme/api.git" }) psi.ArgumentList.Add(arg);
            psi.Environment["HOME"] = home;
            psi.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            psi.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(home, "gitconfig");
            psi.Environment["GIT_TERMINAL_PROMPT"] = "0";

            using var git = Process.Start(psi)!;
            using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output = git.StandardOutput.ReadToEndAsync(patience.Token);
            var errors = git.StandardError.ReadToEndAsync(patience.Token);

            await git.WaitForExitAsync(patience.Token).ConfigureAwait(false);

            return (git.ExitCode, await output.ConfigureAwait(false) + await errors.ConfigureAwait(false));
        }
        finally
        {
            Directory.Delete(home, recursive: true);
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, string signal)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);

        while (!condition())
        {
            if (DateTimeOffset.UtcNow > deadline) throw new TimeoutException($"{signal} within 10s");

            await Task.Delay(20).ConfigureAwait(false);
        }
    }
}

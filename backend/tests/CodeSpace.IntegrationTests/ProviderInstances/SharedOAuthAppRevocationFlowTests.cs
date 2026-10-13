using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Commands.ProviderInstances;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Shouldly;

namespace CodeSpace.IntegrationTests.ProviderInstances;

/// <summary>
/// Two teams connect GitHub through the same OAuth App — the unique index is per team, so nothing stops them sharing a
/// client id — and the same person authorizes it in both. GitHub keeps one grant per user and app: its grant delete ends
/// every token issued under it, the other team's included. Deleting one team's connection must end only the tokens that
/// team held; a Disconnect keeps ending the whole grant, as it always has. The same holds for the pair GitHub issues a
/// refresh that a retirement or a Disconnect overtakes.
///
/// <para>Drives the real chain: the mediator pipeline and its transaction, <c>ProviderInstanceService</c> and
/// <c>CredentialService</c>, the post-commit revoke, the real <c>GitHubOAuthClient</c> speaking HTTP, and Postgres —
/// against a GitHub on a loopback port that keeps grants the way GitHub does, so what each call ends is read off the
/// provider's own state rather than inferred from which URL was hit.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Integration")]
public class SharedOAuthAppRevocationFlowTests
{
    private const string ClientSecret = "shared-app-secret";

    private readonly PostgresFixture _fixture;

    public SharedOAuthAppRevocationFlowTests(PostgresFixture fixture) { _fixture = fixture; }

    /// <summary>What lands while team A's refresh is at GitHub and blanks the token it read.</summary>
    public enum RetiringWrite
    {
        /// <summary>This build's connection delete: it retires the credential (blanked, Revoked, soft-deleted) and ends team A's token by token.</summary>
        ConnectionDelete,

        /// <summary>The previous build's connection delete — Revoked and soft-deleted, token kept, nothing asked of GitHub — then this build's sweep blanking it: a rolling deploy or a rollback.</summary>
        PreviousBuildDeleteThenSweep
    }

    [Fact]
    public async Task Deleting_one_teams_connection_ends_only_that_teams_token_and_not_the_grant_another_team_shares()
    {
        using var gitHub = new StubProviderHost();
        var world = await SeedSharedAppAsync(gitHub, gitHub);

        await DeleteConnectionAsync(world.TeamA, world.InstanceA);

        world.Grants.IsLive(world.AccessTokenA).ShouldBeFalse($"team A's token must end at GitHub — check the post-commit revoke in ProviderInstanceService; GitHub saw: {Seen(gitHub)}");
        world.Grants.IsLive(world.AccessTokenB).ShouldBeTrue($"team B connected through the same OAuth App and must keep working; a grant delete would have ended its token too. GitHub saw: {Seen(gitHub)}");
        gitHub.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/grant"), "a connection delete never asks GitHub for the user's whole grant");
        gitHub.Requests.ShouldNotContain(r => r.Body.Contains(world.RefreshTokenA), "GitHub has no endpoint that ends a refresh token alone, so none is sent");
        (await StoredAccessTokenAsync(world.CredentialB)).ShouldBe(world.AccessTokenB, "the other team's credential is untouched here too");
    }

    [Fact]
    public async Task A_disconnect_still_ends_the_whole_grant()
    {
        using var gitHub = new StubProviderHost();
        var world = await SeedSharedAppAsync(gitHub, gitHub);

        var result = await DisconnectAsync(world.TeamB, world.CredentialB);

        result.ProviderAcknowledged.ShouldBeTrue($"GitHub answered the grant delete; saw: {Seen(gitHub)}");
        gitHub.Requests.ShouldContain(r => r.Method == "DELETE" && r.PathAndQuery.EndsWith($"/applications/{world.ClientId}/grant", StringComparison.Ordinal), "a Disconnect ends the user's grant to the app, unchanged");
        world.Grants.IsLive(world.AccessTokenB).ShouldBeFalse();
        world.Grants.IsLive(world.AccessTokenA).ShouldBeFalse("the grant is the user's, not the team's: ending it ends the token the other team holds too — which is why a connection delete must not ask for it");
    }

    /// <summary>
    /// Team A's token is inside the refresh window, and the connection is retired while GitHub holds the refresh: the
    /// rotated pair GitHub then issues is stored nowhere, so the refresher ends it at GitHub itself. A retirement ends
    /// tokens, never the person's grant — the same rule the delete's own revoke follows — so team B, connected through the
    /// same OAuth App by the same person, keeps working. The refresher tells a retirement from a Disconnect by the
    /// credential's soft delete, which only a retirement writes.
    /// </summary>
    [Theory]
    [InlineData(RetiringWrite.ConnectionDelete)]
    [InlineData(RetiringWrite.PreviousBuildDeleteThenSweep)]
    public async Task A_connection_retired_while_its_refresh_is_at_GitHub_ends_only_the_pair_GitHub_issued(RetiringWrite retirement)
    {
        using var gitHub = new StubProviderHost();
        using var signIn = new StubProviderHost();
        var world = await SeedSharedAppAsync(gitHub, signIn);
        world.Grants.ServeRefresh(signIn, "octocat", world.RotatedAccessTokenA, world.RotatedRefreshTokenA, whileRefreshing: () => RetireAsync(world, retirement).GetAwaiter().GetResult());

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => ResolveTokenAsync(world.CredentialA));

        failure.Message.ShouldContain("was revoked", customMessage: "fixture check: the retirement blanked the row while GitHub answered, so the refresh found it empty");
        world.Grants.IsLive(world.RotatedAccessTokenA).ShouldBeFalse($"the pair issued while the connection was retired is held by nothing and must end at GitHub; GitHub saw: {Seen(gitHub)}");
        gitHub.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/grant"), $"a retirement racing a refresh never asks GitHub for the user's whole grant — check OAuthTokenRefresher.RevokeIssuedTokensAsync; GitHub saw: {Seen(gitHub)}");
        world.Grants.IsLive(world.AccessTokenB).ShouldBeTrue("team B connected through the same OAuth App and must keep working");
        gitHub.Requests.ShouldNotContain(r => r.Body.Contains(world.RotatedRefreshTokenA), "GitHub has no endpoint that ends a refresh token alone, so none is sent");
    }

    /// <summary>The sibling of the retirement race: a Disconnect landing while the refresh is at GitHub still ends the grant, the pair just issued included.</summary>
    [Fact]
    public async Task A_disconnect_while_a_refresh_is_at_GitHub_still_ends_the_whole_grant()
    {
        using var gitHub = new StubProviderHost();
        using var signIn = new StubProviderHost();
        var world = await SeedSharedAppAsync(gitHub, signIn);
        world.Grants.ServeRefresh(signIn, "octocat", world.RotatedAccessTokenA, world.RotatedRefreshTokenA, whileRefreshing: () => DisconnectAsync(world.TeamA, world.CredentialA).GetAwaiter().GetResult());

        var failure = await Should.ThrowAsync<InvalidOperationException>(() => ResolveTokenAsync(world.CredentialA));

        failure.Message.ShouldContain("was revoked", customMessage: "fixture check: the Disconnect blanked the row while GitHub answered, so the refresh found it empty");
        gitHub.Requests.ShouldContain(r => r.Method == "DELETE" && r.PathAndQuery.EndsWith($"/applications/{world.ClientId}/grant", StringComparison.Ordinal) && r.Body.Contains(world.RotatedAccessTokenA), $"the pair issued during a Disconnect ends the way the Disconnect does — the whole grant; GitHub saw: {Seen(gitHub)}");
        world.Grants.IsLive(world.RotatedAccessTokenA).ShouldBeFalse();
    }

    private sealed record SharedApp(GitHubGrants Grants, string ClientId, Guid TeamA, Guid InstanceA, Guid CredentialA, string AccessTokenA, string RefreshTokenA, string RotatedAccessTokenA, string RotatedRefreshTokenA, Guid TeamB, Guid InstanceB, Guid CredentialB, string AccessTokenB);

    /// <summary>
    /// The same person connected in two teams, each team with its own GitHub connection on the same host and OAuth App —
    /// two credentials, two access tokens, one grant at GitHub. <paramref name="gitHub"/> is GitHub's API, where tokens are
    /// revoked; <paramref name="signIn"/> its sign-in host, where they are refreshed — two listeners, so a write landing
    /// while a refresh is answered can reach the API. Team A's token is inside the refresh window; team B's is not.
    /// </summary>
    private async Task<SharedApp> SeedSharedAppAsync(StubProviderHost gitHub, StubProviderHost signIn)
    {
        var canary = Guid.NewGuid().ToString("N")[..10];
        var clientId = $"shared-app-{canary}";
        var grants = GitHubGrants.Serve(gitHub, clientId, ClientSecret);
        var (teamA, instanceA) = await SeedConnectionAsync(signIn.BaseUrl, gitHub.BaseUrl, clientId).ConfigureAwait(false);
        var (teamB, instanceB) = await SeedConnectionAsync(signIn.BaseUrl, gitHub.BaseUrl, clientId).ConfigureAwait(false);
        var credentialA = await SeedOAuthCredentialAsync(teamA, instanceA, $"at-A-{canary}", $"rt-A-{canary}", DateTimeOffset.UtcNow.AddSeconds(30)).ConfigureAwait(false);
        var credentialB = await SeedOAuthCredentialAsync(teamB, instanceB, $"at-B-{canary}", $"rt-B-{canary}", DateTimeOffset.UtcNow.AddHours(8)).ConfigureAwait(false);

        grants.Issue("octocat", $"at-A-{canary}", $"at-B-{canary}");

        return new SharedApp(grants, clientId, teamA, instanceA, credentialA, $"at-A-{canary}", $"rt-A-{canary}", $"at-A2-{canary}", $"rt-A2-{canary}", teamB, instanceB, credentialB, $"at-B-{canary}");
    }

    private async Task<(Guid TeamId, Guid InstanceId)> SeedConnectionAsync(string signInUrl, string apiUrl, string clientId)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team { Id = Guid.NewGuid(), Slug = $"shared-app-{suffix}", Name = "Shared app" };
        var instance = new ProviderInstance
        {
            Id = Guid.NewGuid(), TeamId = team.Id, Provider = ProviderKind.GitHub, DisplayName = "GitHub", BaseUrl = signInUrl, ApiUrl = apiUrl,
            OauthClientId = clientId, OauthClientSecretEnc = scope.Resolve<IPayloadEncryptor>().Encrypt(ClientSecret)
        };

        db.Team.Add(team);
        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return (team.Id, instance.Id);
    }

    private async Task<Guid> SeedOAuthCredentialAsync(Guid teamId, Guid instanceId, string accessToken, string refreshToken, DateTimeOffset expiresAt)
    {
        using var scope = _fixture.BeginScope();
        var db = scope.Resolve<CodeSpaceDbContext>();
        var payload = scope.Resolve<ICredentialPayloadSerializer>().Serialize(new OAuthPayload { AccessToken = accessToken, RefreshToken = refreshToken, ExpiresAt = expiresAt });
        var credential = new Credential
        {
            Id = Guid.NewGuid(), TeamId = teamId, ProviderInstanceId = instanceId, Ownership = CredentialOwnership.TeamService, AuthType = AuthType.OAuth,
            DisplayName = "octocat", EncryptedPayload = scope.Resolve<IPayloadEncryptor>().Encrypt(payload), ExpiresDate = expiresAt, Status = CredentialStatus.Active
        };

        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }

    private async Task DeleteConnectionAsync(Guid teamId, Guid instanceId)
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);

        await scope.Resolve<IMediator>().Send(new DeleteProviderInstanceCommand { ProviderInstanceId = instanceId }).ConfigureAwait(false);
    }

    private async Task<RevokeCredentialResult> DisconnectAsync(Guid teamId, Guid credentialId)
    {
        using var scope = _fixture.BeginScopeAs(Guid.NewGuid(), teamId, Roles.Admin);

        return await scope.Resolve<IMediator>().Send(new RevokeCredentialCommand { CredentialId = credentialId }).ConfigureAwait(false);
    }

    private Task RetireAsync(SharedApp world, RetiringWrite retirement) => retirement switch
    {
        RetiringWrite.ConnectionDelete => DeleteConnectionAsync(world.TeamA, world.InstanceA),
        RetiringWrite.PreviousBuildDeleteThenSweep => DeleteAsThePreviousBuildThenSweepAsync(world.InstanceA),
        _ => throw new ArgumentOutOfRangeException(nameof(retirement), retirement, null)
    };

    /// <summary>
    /// The previous build's <c>ProviderInstanceService.DeleteAsync</c>, statement for statement — cascade-revoke then soft
    /// delete, every token kept, nothing asked of GitHub — replayed as raw SQL against this build's schema, then this
    /// build's recurring sweep through the real mediator, which blanks the token that delete kept.
    /// </summary>
    private async Task DeleteAsThePreviousBuildThenSweepAsync(Guid instanceId)
    {
        await using (var connection = new NpgsqlConnection(_fixture.ConnectionString))
        {
            await connection.OpenAsync().ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                """
                UPDATE credential SET status = 'Revoked', deleted_date = now(), last_error = 'Provider instance deleted' WHERE provider_instance_id = @deleted AND deleted_date IS NULL AND status = 'Active';
                UPDATE provider_instance SET deleted_date = now() WHERE id = @deleted;
                """, connection);
            command.Parameters.AddWithValue("deleted", instanceId);
            await command.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        using var scope = _fixture.BeginScope();
        await scope.Resolve<IMediator>().Send(new ShredRetiredSecretsCommand()).ConfigureAwait(false);
    }

    /// <summary>A run resolving the credential's token: the real auth resolver → GitHub OAuth strategy → refresher chain, on the row as a run loads it.</summary>
    private async Task<string> ResolveTokenAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var credential = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Include(c => c.ProviderInstance).SingleAsync(c => c.Id == credentialId).ConfigureAwait(false);
        var auth = await scope.Resolve<IProviderAuthResolver>().ResolveAsync(new ProviderContext(credential.ProviderInstance, credential), CancellationToken.None).ConfigureAwait(false);

        return auth.Token;
    }

    private async Task<string> StoredAccessTokenAsync(Guid credentialId)
    {
        using var scope = _fixture.BeginScope();
        var stored = await scope.Resolve<CodeSpaceDbContext>().Credential.AsNoTracking().Where(c => c.Id == credentialId).Select(c => c.EncryptedPayload).SingleAsync().ConfigureAwait(false);
        var payload = scope.Resolve<ICredentialPayloadSerializer>().Deserialize(AuthType.OAuth, scope.Resolve<IPayloadEncryptor>().Decrypt(stored));

        return payload.ShouldBeOfType<OAuthPayload>().AccessToken;
    }

    private static string Seen(StubProviderHost gitHub) => string.Join(", ", gitHub.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"));

    /// <summary>
    /// A GitHub OAuth App's tokens as GitHub keeps them: each access token issued to one user, all of a user's tokens one
    /// grant. <c>DELETE /applications/{client_id}/grant</c> ends every token of the user the presented token belongs to;
    /// <c>DELETE /applications/{client_id}/token</c> ends the presented token alone. Both answer 204, 404 for a token
    /// GitHub does not know, and 401 unless the request authenticates as the app.
    /// </summary>
    private sealed class GitHubGrants
    {
        private readonly ConcurrentDictionary<string, string> _userByLiveToken = new(StringComparer.Ordinal);
        private readonly string _basicAuth;

        private GitHubGrants(string clientId, string clientSecret) { _basicAuth = "Basic " + Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}")); }

        public static GitHubGrants Serve(StubProviderHost host, string clientId, string clientSecret)
        {
            var grants = new GitHubGrants(clientId, clientSecret);

            host.Answer("DELETE", $"/applications/{clientId}/grant", grants.DeleteGrant);
            host.Answer("DELETE", $"/applications/{clientId}/token", grants.DeleteToken);

            return grants;
        }

        public void Issue(string user, params string[] accessTokens)
        {
            foreach (var token in accessTokens) _userByLiveToken[token] = user;
        }

        public bool IsLive(string accessToken) => _userByLiveToken.ContainsKey(accessToken);

        /// <summary>
        /// GitHub's token endpoint answering a refresh: <paramref name="whileRefreshing"/> runs while GitHub holds the
        /// request — a write that lands before its answer — then the pair GitHub issues <paramref name="user"/> is live and
        /// returned. The sign-in host is not the API host, so that write can reach the API meanwhile.
        /// </summary>
        public void ServeRefresh(StubProviderHost signIn, string user, string accessToken, string refreshToken, Action whileRefreshing)
        {
            signIn.Answer("POST", "/login/oauth/access_token", _ =>
            {
                whileRefreshing();
                Issue(user, accessToken);

                return new StubProviderHost.StubReply(200, JsonSerializer.Serialize(new { access_token = accessToken, refresh_token = refreshToken, expires_in = 28800, refresh_token_expires_in = 15897600, token_type = "bearer", scope = "" }));
            });
        }

        private StubProviderHost.StubReply DeleteGrant(StubProviderHost.RecordedRequest request)
        {
            if (request.AuthorizationHeader != _basicAuth) return new StubProviderHost.StubReply(401, """{"message":"Requires authentication"}""");
            if (!_userByLiveToken.TryGetValue(AccessToken(request), out var user)) return new StubProviderHost.StubReply(404, """{"message":"Not Found"}""");

            foreach (var token in _userByLiveToken.Where(entry => entry.Value == user).Select(entry => entry.Key).ToList()) _userByLiveToken.TryRemove(token, out _);

            return new StubProviderHost.StubReply(204, string.Empty);
        }

        private StubProviderHost.StubReply DeleteToken(StubProviderHost.RecordedRequest request)
        {
            if (request.AuthorizationHeader != _basicAuth) return new StubProviderHost.StubReply(401, """{"message":"Requires authentication"}""");

            return _userByLiveToken.TryRemove(AccessToken(request), out _) ? new StubProviderHost.StubReply(204, string.Empty) : new StubProviderHost.StubReply(404, """{"message":"Not Found"}""");
        }

        private static string AccessToken(StubProviderHost.RecordedRequest request) => JsonDocument.Parse(request.Body).RootElement.GetProperty("access_token").GetString()!;
    }
}

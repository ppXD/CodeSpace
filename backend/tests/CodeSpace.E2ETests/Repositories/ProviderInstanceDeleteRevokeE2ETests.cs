using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Repositories;

/// <summary>
/// Deleting a git provider connection ends every token connected through it, here and at the provider. Before, the
/// delete marked them Revoked and left the tokens and the connection's OAuth client secret decryptable in the database,
/// and never told the provider, so an OAuth refresh token stayed good at GitLab while the UI said "Revoked".
///
/// <para>Driven through the real HTTP surface (POST and DELETE /api/provider-instances, POST /api/credentials/pat,
/// GET /api/credentials), the real mediator pipeline and its transaction, the real <c>GitLabOAuthClient</c> speaking
/// RFC 7009 over HTTP, and Postgres, against a GitLab on a loopback port that records every request. One seam: the OAuth
/// credential is written straight to the database, sealed by the production encryptor, as an OAuth connect would leave it
/// — the connect flow is not what is under test.</para>
///
/// <para>Fidelity: HIGH. Only GitLab (and GitHub, for the shared-OAuth-App case) is a stub, and it is a real socket the
/// production client speaks HTTP to.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ProviderInstanceDeleteRevokeE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public ProviderInstanceDeleteRevokeE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task Deleting_a_connection_revokes_its_oauth_grants_at_the_provider_and_leaves_no_secret_decryptable()
    {
        await using var gitLab = RecordingProviderStub.Start();
        var canary = Guid.NewGuid().ToString("N");
        var world = await SeedConnectionAsync(gitLab.BaseUrl, canary);

        var delete = await _tenants.SendAsync(HttpMethod.Delete, $"/api/provider-instances/{world.InstanceId}", world.Admin, world.Team);

        delete.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(delete));
        (await delete.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revokedCredentialCount").GetInt32().ShouldBe(2);

        var revokes = gitLab.Requests.Where(r => r.Method == "POST" && r.PathAndQuery == "/oauth/revoke").ToList();
        revokes.Select(r => (r.Form["token"], r.Form["token_type_hint"])).ShouldBe(new[] { ($"rt-{canary}", "refresh_token"), ($"at-{canary}", "access_token") }, "GitLab must be asked to revoke the refresh token, then the access token — check the post-commit revoke in ProviderInstanceService and the stub's request log if this fails");
        revokes.ShouldAllBe(r => r.Form["client_id"] == world.ClientId && r.Form["client_secret"] == $"client-secret-{canary}");
        gitLab.Carrying($"glpat-{canary}").ShouldBeEmpty("a personal access token has no revocation endpoint and is never sent anywhere by a delete");

        var stored = await StoredAsync(world);
        stored.Payloads.ShouldAllBe(p => p == string.Empty, "a deleted connection's tokens must not stay decryptable in the database");
        stored.ClientSecret.ShouldBeNull("nor its OAuth client secret");

        var listed = await _tenants.SendAsync(HttpMethod.Get, "/api/credentials", world.Owner, world.Team);
        (await listed.Content.ReadAsStringAsync()).ShouldNotContain(world.OAuthCredentialId.ToString(), Case.Insensitive, "a retired credential is out of the team's list");
    }

    [Fact]
    public async Task Deleting_a_connection_whose_provider_is_down_still_retires_everything_here()
    {
        var down = $"http://127.0.0.1:{RecordingProviderStub.FreeLoopbackPort()}";
        var canary = Guid.NewGuid().ToString("N");
        var world = await SeedConnectionAsync(down, canary);

        var delete = await _tenants.SendAsync(HttpMethod.Delete, $"/api/provider-instances/{world.InstanceId}", world.Admin, world.Team);

        delete.StatusCode.ShouldBe(HttpStatusCode.OK, $"the provider-side revoke is best effort; a provider that does not answer must not fail the delete — {await TenantHarness.DescribeAsync(delete)}");

        var stored = await StoredAsync(world);
        stored.Payloads.ShouldAllBe(p => p == string.Empty);
        stored.ClientSecret.ShouldBeNull();
    }

    /// <summary>
    /// Two teams connect GitHub through the same OAuth App and the same person authorizes it in both. GitHub's grant delete
    /// ends every token the person holds under the app, the other team's included, so deleting one team's connection asks
    /// GitHub to end only the access token that team held — and sends no refresh token, which GitHub cannot end alone. A
    /// Disconnect still ends the whole grant, as it always has.
    /// </summary>
    [Fact]
    public async Task Deleting_a_GitHub_connection_ends_only_its_own_token_while_a_disconnect_still_ends_the_grant()
    {
        await using var gitHub = RecordingProviderStub.Start();
        var canary = Guid.NewGuid().ToString("N");
        var clientId = $"shared-app-{canary}";
        var deleted = await SeedGitHubConnectionAsync(gitHub.BaseUrl, clientId, $"A-{canary}");
        var kept = await SeedGitHubConnectionAsync(gitHub.BaseUrl, clientId, $"B-{canary}");

        var delete = await _tenants.SendAsync(HttpMethod.Delete, $"/api/provider-instances/{deleted.InstanceId}", deleted.Admin, deleted.Team);

        delete.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(delete));
        var revokes = gitHub.Requests.Where(r => r.Method == "DELETE" && r.PathAndQuery.StartsWith("/api/v3/applications/", StringComparison.Ordinal)).ToList();
        revokes.Select(r => (r.PathAndQuery, r.Body)).ShouldBe(new[] { ($"/api/v3/applications/{clientId}/token", $"{{\"access_token\":\"at-A-{canary}\"}}") }, $"a delete ends the one access token its team held, never the person's grant, which team B's token lives under too — check the post-commit revoke in ProviderInstanceService; requests seen: {string.Join(", ", gitHub.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"))}");
        gitHub.Carrying($"rt-A-{canary}").ShouldBeEmpty("GitHub has no endpoint that ends a refresh token alone, so none is sent");
        gitHub.Carrying($"at-B-{canary}").ShouldBeEmpty("team B's token is never presented by team A's delete");

        var disconnect = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{kept.OAuthCredentialId}/revoke", kept.Owner, kept.Team);

        disconnect.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(disconnect));
        gitHub.Carrying($"at-B-{canary}").ShouldHaveSingleItem().PathAndQuery.ShouldBe($"/api/v3/applications/{clientId}/grant", "a Disconnect ends the person's whole grant to the app, unchanged");
    }

    /// <summary>
    /// Team A's token is inside the refresh window and a listing refreshes it; team A's connection is deleted while GitHub
    /// holds that refresh. The pair GitHub then issues is stored nowhere, so the refresher ends it at GitHub — by token, as
    /// the delete itself ends team A's token: a grant delete would end the person's token in team B too.
    /// </summary>
    [Fact]
    public async Task A_GitHub_connection_deleted_while_its_token_refreshes_ends_only_the_pair_GitHub_issued()
    {
        await using var gitHub = RecordingProviderStub.Start();
        var canary = Guid.NewGuid().ToString("N");
        var clientId = $"shared-app-{canary}";
        var deleted = await SeedGitHubConnectionAsync(gitHub.BaseUrl, clientId, $"A-{canary}", DateTimeOffset.UtcNow.AddSeconds(30));
        await SeedGitHubConnectionAsync(gitHub.BaseUrl, clientId, $"B-{canary}");
        HttpStatusCode? deleteStatus = null;
        gitHub.AnswerRefresh($"at-A2-{canary}", $"rt-A2-{canary}", whileRefreshing: async () => deleteStatus = (await _tenants.SendAsync(HttpMethod.Delete, $"/api/provider-instances/{deleted.InstanceId}", deleted.Admin, deleted.Team)).StatusCode);

        var listing = await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{deleted.OAuthCredentialId}/accessible-repositories", deleted.Owner, deleted.Team);

        deleteStatus.ShouldBe(HttpStatusCode.OK, $"fixture check: the delete landed while GitHub held the refresh; requests seen: {string.Join(", ", gitHub.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"))}");
        listing.IsSuccessStatusCode.ShouldBeFalse("a listing whose connection was deleted under it gets no token");
        var revokes = gitHub.Requests.Where(r => r.Method == "DELETE" && r.PathAndQuery.StartsWith("/api/v3/applications/", StringComparison.Ordinal)).ToList();
        revokes.Select(r => (r.PathAndQuery, r.Body)).ShouldBe(new[] { ($"/api/v3/applications/{clientId}/token", $"{{\"access_token\":\"at-A-{canary}\"}}"), ($"/api/v3/applications/{clientId}/token", $"{{\"access_token\":\"at-A2-{canary}\"}}") }, $"the delete ends the token team A held, then the refresher ends the pair GitHub issued meanwhile — each by token, never the person's grant, which team B's token lives under too; check OAuthTokenRefresher.RevokeIssuedTokensAsync. Requests seen: {string.Join(", ", gitHub.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"))}");
        gitHub.Carrying($"at-B-{canary}").ShouldBeEmpty("team B's token is never presented by team A's delete or its refresh");
    }

    /// <summary>
    /// A connection-wide connection whose group hook was registered at GitLab with the owner's personal access token. The
    /// delete blanks that token, so the hook must come down first, while the token still opens: afterwards nothing could
    /// remove it, and it would go on delivering the group's events with its HMAC secret still decryptable here. A hook
    /// row retired earlier stays as the record it is, holding no secret.
    /// </summary>
    [Fact]
    public async Task Deleting_a_connection_wide_connection_takes_its_group_hook_down_at_the_provider_before_its_token_goes()
    {
        await using var gitLab = RecordingProviderStub.Start();
        var canary = Guid.NewGuid().ToString("N");
        var world = await SeedConnectionAsync(gitLab.BaseUrl, canary);
        var retiredEarlier = await SeedGroupHooksAsync(world, $"acme-{canary}", $"hook-{canary}", canary);

        var delete = await _tenants.SendAsync(HttpMethod.Delete, $"/api/provider-instances/{world.InstanceId}", world.Admin, world.Team);

        delete.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(delete));

        var hookDeletes = gitLab.Requests.Where(r => r.Method == "DELETE" && r.PathAndQuery == $"/api/v4/groups/acme-{canary}/hooks/hook-{canary}").ToList();
        hookDeletes.Count.ShouldBe(1, $"GitLab must be asked to delete the group hook — check the hook retirement in ProviderInstanceService.DeleteAsync; requests seen: {string.Join(", ", gitLab.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"))}");
        hookDeletes[0].Headers.ShouldContain($"glpat-{canary}", Case.Sensitive, "the hook is deleted with the token that registered it, before the delete blanks that token");

        var hooks = await _tenants.DbAsync(db => db.ConnectionWebhook.AsNoTracking().Where(w => w.ProviderInstanceId == world.InstanceId).ToListAsync());
        hooks.Select(h => h.Id).ShouldBe(new[] { retiredEarlier }, "the Registered row described the hook that is now gone");
        hooks.ShouldAllBe(h => h.RegistrationStatus == RepositoryWebhookRegistrationStatus.Cancelled && h.SecretEnc == string.Empty, "a deleted connection keeps no hook secret decryptable");
    }

    private sealed record Connection(Guid Owner, Guid Admin, Guid Team, Guid InstanceId, string ClientId, Guid OAuthCredentialId, Guid PatCredentialId);

    /// <summary>
    /// Puts the connection in connection-wide scope with a Registered group hook on <paramref name="ownerPath"/>, registered
    /// by the owner's personal access token, beside a Cancelled one an earlier scope switch left. Written directly, as the
    /// registrar leaves them — the registration is not what is under test. Returns the Cancelled row's id.
    /// </summary>
    private async Task<Guid> SeedGroupHooksAsync(Connection world, string ownerPath, string externalId, string canary)
    {
        using var scope = _factory.Services.CreateScope();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var registered = Guid.NewGuid();
        var cancelled = Guid.NewGuid();

        await _tenants.DbAsync(async db =>
        {
            var instance = await db.ProviderInstance.SingleAsync(p => p.Id == world.InstanceId);
            instance.WebhookScope = ProviderWebhookScope.Connection;

            db.ConnectionWebhook.Add(GroupHook(registered, world, ownerPath, externalId, RepositoryWebhookRegistrationStatus.Registered, encryptor.Encrypt($"hook-secret-{canary}")));
            db.ConnectionWebhook.Add(GroupHook(cancelled, world, $"{ownerPath}-old", externalId: null, RepositoryWebhookRegistrationStatus.Cancelled, encryptor.Encrypt($"old-hook-secret-{canary}")));

            return await db.SaveChangesAsync();
        });

        return cancelled;
    }

    private static ConnectionWebhook GroupHook(Guid id, Connection world, string ownerPath, string? externalId, RepositoryWebhookRegistrationStatus status, string secretEnc) => new()
    {
        Id = id, ProviderInstanceId = world.InstanceId, CredentialId = world.PatCredentialId, OwnerPath = ownerPath, ExternalId = externalId,
        CallbackUrl = $"https://codespace.test/api/webhooks/connection/{id}", SecretEnc = secretEnc, SubscribedEvents = new List<string> { "push" }, RegistrationStatus = status,
    };

    /// <summary>
    /// The Owner adds a GitLab connection with an OAuth app and a personal access token through the API; the Owner's OAuth
    /// credential is sealed by the production encryptor and written directly, as a completed connect leaves it.
    /// </summary>
    private async Task<Connection> SeedConnectionAsync(string gitLabUrl, string canary)
    {
        var owner = await _tenants.AccountAsync();
        var admin = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (admin, TeamRole.Admin));
        var clientId = $"client-{canary}";
        var instanceId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/provider-instances", owner, team, new { provider = "GitLab", displayName = "gitlab", baseUrl = gitLabUrl, oauthClientId = clientId, oauthClientSecret = $"client-secret-{canary}" }));
        var patId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/pat", owner, team, new { providerInstanceId = instanceId, displayName = "owner pat", token = $"glpat-{canary}" }));
        var oauthId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var payload = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>().Serialize(new OAuthPayload { AccessToken = $"at-{canary}", RefreshToken = $"rt-{canary}" });
            var sealedPayload = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Encrypt(payload);

            await _tenants.DbAsync(async db =>
            {
                db.Credential.Add(new Credential { Id = oauthId, TeamId = team, ProviderInstanceId = instanceId, OwnerUserId = owner, Ownership = CredentialOwnership.Personal, AuthType = AuthType.OAuth, DisplayName = "owner oauth", EncryptedPayload = sealedPayload, Status = CredentialStatus.Active });
                return await db.SaveChangesAsync();
            });
        }

        return new Connection(owner, admin, team, instanceId, clientId, oauthId, patId);
    }

    private sealed record GitHubConnection(Guid Owner, Guid Admin, Guid Team, Guid InstanceId, Guid OAuthCredentialId);

    /// <summary>
    /// A team whose Owner adds a GitHub connection on <paramref name="gitHubUrl"/> through the API with the OAuth App
    /// <paramref name="clientId"/>, and the Owner's OAuth credential (<c>at-{tag}</c>, <c>rt-{tag}</c>, expiring at
    /// <paramref name="expiresAt"/> when given) sealed by the production encryptor and written directly, as a completed
    /// connect leaves it.
    /// </summary>
    private async Task<GitHubConnection> SeedGitHubConnectionAsync(string gitHubUrl, string clientId, string tag, DateTimeOffset? expiresAt = null)
    {
        var owner = await _tenants.AccountAsync();
        var admin = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (admin, TeamRole.Admin));
        var instanceId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/provider-instances", owner, team, new { provider = "GitHub", displayName = "github", baseUrl = gitHubUrl, oauthClientId = clientId, oauthClientSecret = "shared-app-secret" }));
        var oauthId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var payload = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>().Serialize(new OAuthPayload { AccessToken = $"at-{tag}", RefreshToken = $"rt-{tag}", ExpiresAt = expiresAt });
            var sealedPayload = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Encrypt(payload);

            await _tenants.DbAsync(async db =>
            {
                db.Credential.Add(new Credential { Id = oauthId, TeamId = team, ProviderInstanceId = instanceId, OwnerUserId = owner, Ownership = CredentialOwnership.Personal, AuthType = AuthType.OAuth, DisplayName = "owner oauth", EncryptedPayload = sealedPayload, ExpiresDate = expiresAt, Status = CredentialStatus.Active });
                return await db.SaveChangesAsync();
            });
        }

        return new GitHubConnection(owner, admin, team, instanceId, oauthId);
    }

    private async Task<(IReadOnlyList<string> Payloads, string? ClientSecret)> StoredAsync(Connection world)
    {
        var payloads = await _tenants.DbAsync(db => db.Credential.AsNoTracking().Where(c => c.Id == world.OAuthCredentialId || c.Id == world.PatCredentialId).Select(c => c.EncryptedPayload).ToListAsync());
        var clientSecret = await _tenants.DbAsync(db => db.ProviderInstance.AsNoTracking().Where(p => p.Id == world.InstanceId).Select(p => p.OauthClientSecretEnc).SingleAsync());

        payloads.Count.ShouldBe(2, "fixture check: both credentials were seeded");

        return (payloads, clientSecret);
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}

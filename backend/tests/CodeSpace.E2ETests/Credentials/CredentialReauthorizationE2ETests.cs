using System.Net;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit.Abstractions;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity — a credential re-authorized IN PLACE through the real HTTP surface: the real ASP.NET pipeline (routing,
/// JWT, the X-Team-Id scope, model binding, the credential authorization behaviors, the exception filter), cookies on the
/// wire, the real GitLab OAuth client and GitLab provider, real Postgres. The one fake is GitLab, a
/// <see cref="GitLabDoorkeeperStub"/> on a loopback port.
///
/// <para>What this pins end to end: <c>POST /api/credentials/oauth/init</c> naming a <c>replaceCredentialId</c>, then the
/// provider's callback — completing in the browser that started it, or handed to the SPA's completion page and finished by
/// <c>POST /api/credentials/oauth/complete</c> when the SPA and the API are on different sites — leaves the SAME credential
/// id holding the new pair, with its repository still bound to it and now working; <c>PUT /api/credentials/{id}/token</c> does
/// the same for a pasted token; and each refusal reaches a client as its own status and code.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "ProviderE2E")]
[Trait("Surface", "Http")]
public sealed class CredentialReauthorizationE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string BindingCookie = "codespace_oauth_nonce";
    private const string CallbackPath = "/api/credentials/oauth/callback";
    private const string CompletePath = "/api/credentials/oauth/complete";

    /// <summary>A dev SPA origin on another site than the API's http://localhost — on the CORS list in Development.</summary>
    private const string CrossSiteSpa = "http://127.0.0.1:5180";

    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;
    private readonly ITestOutputHelper _output;

    public CredentialReauthorizationE2ETests(TaskLaunchApiFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
        _output = output;
    }

    [Fact]
    public async Task A_reconnect_completes_in_the_starting_browser_on_the_same_credential_and_the_repository_works_again()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, deadPair: true).ConfigureAwait(false);
        (await ReadListedAsync(world.OwnerId, world.TeamId, world.CredentialId).ConfigureAwait(false)).GetProperty("health").GetString().ShouldBe("rejected", "fixture check: the Providers list shows the refused credential as one to reconnect");

        var init = await InitAsync(world, "/settings/credentials", world.CredentialId).ConfigureAwait(false);

        init.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(init).ConfigureAwait(false));
        var callback = await CallbackAsync(await StateOfAsync(init).ConfigureAwait(false), "authz:nacho", CookieValue(BindingCookieOf(init)!)).ConfigureAwait(false);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback).ConfigureAwait(false));
        callback.Headers.Location!.OriginalString.ShouldBe($"/settings/credentials?oauthCredentialId={world.CredentialId}", "the popup is told the credential it already had");

        Dump(gitlab);
        var credential = await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false);
        credential.Status.ShouldBe(CredentialStatus.Active);
        credential.LastError.ShouldBeNull();
        credential.StoredPair.RefreshToken.ShouldBe(gitlab.LiveRefreshTokenOf("nacho"), "the row holds the pair GitLab issued for this sign-in");
        (await _tenants.DbAsync(db => db.Credential.AsNoTracking().CountAsync(c => c.TeamId == world.TeamId)).ConfigureAwait(false)).ShouldBe(1, "no second credential was minted");

        var repository = await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == world.RepositoryId)).ConfigureAwait(false);
        repository.CredentialId.ShouldBe(world.CredentialId);
        repository.Status.ShouldBe(RepositoryStatus.Active, "the Error its dead token had set is lifted");

        var served = await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{world.CredentialId}/accessible-repositories", world.OwnerId, world.TeamId).ConfigureAwait(false);
        served.StatusCode.ShouldBe(HttpStatusCode.OK, $"the same credential id is served with the new pair — {await TenantHarness.DescribeAsync(served).ConfigureAwait(false)}");
        gitlab.Count(StubEvent.Refused).ShouldBe(0, $"nothing presented a consumed refresh token: {string.Join(", ", gitlab.RefusedRefreshTokens())}");

        var listed = await ReadListedAsync(world.OwnerId, world.TeamId, world.CredentialId).ConfigureAwait(false);
        listed.GetProperty("health").GetString().ShouldBe("active", "the list reads the credential as the re-authorization left it, with no sweep in between");
        listed.GetProperty("status").GetString().ShouldBe("Active");
        listed.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
        listed.GetProperty("lastValidatedDate").ValueKind.ShouldNotBe(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_cross_site_reconnect_is_finished_by_its_starters_spa_on_the_same_credential()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, deadPair: true).ConfigureAwait(false);
        var init = await InitAsync(world, $"{CrossSiteSpa}/oauth-callback", world.CredentialId).ConfigureAwait(false);
        var state = await StateOfAsync(init).ConfigureAwait(false);

        // The SPA and the API are on different sites, so the binding cookie never comes back: the callback hands the code to
        // the SPA origin the connect named, exchanging nothing.
        var callback = await CallbackAsync(state, "authz:nacho", bindingCookie: null).ConfigureAwait(false);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback).ConfigureAwait(false));
        callback.Headers.Location!.OriginalString.ShouldStartWith($"{CrossSiteSpa}/oauth-callback#oauthCode=");
        gitlab.Count(StubEvent.Authorized).ShouldBe(0, "handing off exchanges nothing");

        var finish = await CompleteAsync(world.OwnerId, world.TeamId, "authz:nacho", state).ConfigureAwait(false);

        finish.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(finish).ConfigureAwait(false));
        JsonDocument.Parse(await finish.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("credentialId").GetGuid().ShouldBe(world.CredentialId);
        (await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false)).StoredPair.RefreshToken.ShouldBe(gitlab.LiveRefreshTokenOf("nacho"));
        (await _tenants.DbAsync(db => db.Credential.AsNoTracking().CountAsync(c => c.TeamId == world.TeamId)).ConfigureAwait(false)).ShouldBe(1);
        (await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.State == state)).ConfigureAwait(false)).ShouldBe(0, "finishing spends the state");
    }

    [Fact]
    public async Task A_forwarded_cross_site_reconnect_is_refused_to_everyone_but_its_starter()
    {
        // The reconnect names a credential, so a victim who finishes it would put THEIR token into the starter's credential row.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, deadPair: true).ConfigureAwait(false);
        var victim = await _tenants.AccountAsync().ConfigureAwait(false);
        await _tenants.AddMemberAsync(world.TeamId, victim, TeamRole.Owner).ConfigureAwait(false);
        var state = await StateOfAsync(await InitAsync(world, $"{CrossSiteSpa}/oauth-callback", world.CredentialId).ConfigureAwait(false)).ConfigureAwait(false);
        var before = await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false);

        var finish = await CompleteAsync(victim, world.TeamId, "authz:victim", state).ConfigureAwait(false);

        finish.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(finish).ConfigureAwait(false));
        gitlab.Count(StubEvent.Authorized).ShouldBe(0, "the victim's code never reached the token endpoint");
        (await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false)).EncryptedPayload.ShouldBe(before.EncryptedPayload);
    }

    [Fact]
    public async Task A_reconnect_as_another_account_is_refused_with_its_own_status_and_code_on_both_doors()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, deadPair: true).ConfigureAwait(false);
        var before = await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false);

        var sameSite = await InitAsync(world, "/settings/credentials", world.CredentialId).ConfigureAwait(false);
        var refusedAtCallback = await CallbackAsync(await StateOfAsync(sameSite).ConfigureAwait(false), "authz:someone-else", CookieValue(BindingCookieOf(sameSite)!)).ConfigureAwait(false);

        var crossSiteState = await StateOfAsync(await InitAsync(world, $"{CrossSiteSpa}/oauth-callback", world.CredentialId).ConfigureAwait(false)).ConfigureAwait(false);
        var refusedAtComplete = await CompleteAsync(world.OwnerId, world.TeamId, "authz:someone-else", crossSiteState).ConfigureAwait(false);

        foreach (var refusal in new[] { refusedAtCallback, refusedAtComplete })
        {
            refusal.StatusCode.ShouldBe(HttpStatusCode.Conflict, await TenantHarness.DescribeAsync(refusal).ConfigureAwait(false));
            var body = JsonDocument.Parse(await refusal.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement;
            body.GetProperty("code").GetString().ShouldBe("credential_account_mismatch");
            body.GetProperty("expectedAccount").GetString().ShouldBe("nacho");
            body.GetProperty("presentedAccount").GetString().ShouldBe("someone-else");
            body.GetProperty("message").GetString().ShouldContain("connect someone-else as its own credential", Case.Insensitive);
        }

        (await ReadCredentialAsync(world.CredentialId).ConfigureAwait(false)).EncryptedPayload.ShouldBe(before.EncryptedPayload, "neither door wrote anything");
    }

    [Fact]
    public async Task Starting_a_reconnect_names_a_credential_of_this_team_only()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, deadPair: true).ConfigureAwait(false);
        var elsewhere = await SeedAsync(gitlab, deadPair: true).ConfigureAwait(false);

        var init = await InitAsync(world, "/settings/credentials", elsewhere.CredentialId).ConfigureAwait(false);

        init.StatusCode.ShouldBe(HttpStatusCode.NotFound, await TenantHarness.DescribeAsync(init).ConfigureAwait(false));
        (await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.TeamId == world.TeamId)).ConfigureAwait(false)).ShouldBe(0);
    }

    [Fact]
    public async Task Starting_a_reconnect_of_a_retired_credential_is_refused_as_retired_and_starts_nothing()
    {
        // What a departure or a connection delete leaves: the token gone, the row soft-deleted. It stays retired.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, deadPair: true).ConfigureAwait(false);
        await _tenants.DbAsync(async db => await db.Credential.Where(c => c.Id == world.CredentialId).ExecuteUpdateAsync(s => s.SetProperty(c => c.EncryptedPayload, string.Empty).SetProperty(c => c.Status, CredentialStatus.Revoked).SetProperty(c => c.DeletedDate, DateTimeOffset.UtcNow))).ConfigureAwait(false);

        var init = await InitAsync(world, "/settings/credentials", world.CredentialId).ConfigureAwait(false);

        init.StatusCode.ShouldBe(HttpStatusCode.Conflict, await TenantHarness.DescribeAsync(init).ConfigureAwait(false));
        JsonDocument.Parse(await init.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("code").GetString().ShouldBe("credential_retired");
        (await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.TeamId == world.TeamId)).ConfigureAwait(false)).ShouldBe(0);
    }

    [Fact]
    public async Task A_pasted_token_replaces_the_credentials_token_in_place_and_reaches_nothing_at_rest_in_the_clear()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, deadPair: false, tokenCredential: true).ConfigureAwait(false);
        var fresh = $"glpat-fresh-{Guid.NewGuid():N}";
        gitlab.AddAccessToken(fresh, "maya", DateTimeOffset.UtcNow.AddDays(90));
        (await ReadListedAsync(world.MayaId, world.TeamId, world.CredentialId).ConfigureAwait(false)).GetProperty("health").GetString().ShouldBe("rejected", "fixture check: the refused token reads as one to replace");

        var replaced = await ReplaceTokenAsync(world.CredentialId, world.MayaId, world.TeamId, fresh).ConfigureAwait(false);

        replaced.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(replaced).ConfigureAwait(false));
        var body = await replaced.Content.ReadAsStringAsync().ConfigureAwait(false);
        body.ShouldNotContain(fresh, Case.Sensitive, "the token is carried in the request body only and never echoed back");
        var json = JsonDocument.Parse(body).RootElement;
        json.GetProperty("credentialId").GetGuid().ShouldBe(world.CredentialId);
        json.GetProperty("providerUsername").GetString().ShouldBe("maya");

        var credential = await ReadTokenCredentialAsync(world.CredentialId).ConfigureAwait(false);
        credential.Status.ShouldBe(CredentialStatus.Active);
        credential.LastError.ShouldBeNull();
        credential.Token.ShouldBe(fresh);
        var repository = await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == world.RepositoryId)).ConfigureAwait(false);
        repository.CredentialId.ShouldBe(world.CredentialId);
        repository.Status.ShouldBe(RepositoryStatus.Active);

        using var scope = _factory.Services.CreateScope();
        var hits = await SecretCanarySweep.FindAsync(_factory.ConnectionString, scope.ServiceProvider.GetRequiredService<CodeSpace.Core.Services.Workflows.Artifacts.IArtifactStore>(), scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>(), world.TeamId, fresh).ConfigureAwait(false);
        hits.ShouldBeEmpty("the new token is stored sealed and appears in no table in the clear");

        var listed = await ReadListedAsync(world.MayaId, world.TeamId, world.CredentialId).ConfigureAwait(false);
        listed.GetProperty("health").GetString().ShouldBe("active", "the token that replaced the refused one is what the list reads");
        listed.GetProperty("status").GetString().ShouldBe("Active");
        listed.GetProperty("lastError").ValueKind.ShouldBe(JsonValueKind.Null);
        listed.GetProperty("expiresDate").ValueKind.ShouldNotBe(JsonValueKind.Null, "the new token's own expiry, as GitLab reports it");
        listed.GetProperty("daysToExpiry").GetInt32().ShouldBeInRange(88, 90);
    }

    [Fact]
    public async Task A_replacement_token_is_refused_with_the_status_and_code_the_client_branches_on()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, deadPair: false, tokenCredential: true).ConfigureAwait(false);
        var otherMember = await _tenants.AccountAsync().ConfigureAwait(false);
        await _tenants.AddMemberAsync(world.TeamId, otherMember, TeamRole.Member).ConfigureAwait(false);
        var elsewhere = await SeedAsync(gitlab, deadPair: false, tokenCredential: true).ConfigureAwait(false);
        gitlab.SeedPersonalAccessToken("nacho", "glpat-nachos");
        gitlab.SeedPersonalAccessToken("maya", "glpat-fresh");

        var anotherAccount = await ReplaceTokenAsync(world.CredentialId, world.MayaId, world.TeamId, "glpat-nachos").ConfigureAwait(false);
        var notWorking = await ReplaceTokenAsync(world.CredentialId, world.MayaId, world.TeamId, "glpat-typo").ConfigureAwait(false);
        var notYours = await ReplaceTokenAsync(world.CredentialId, otherMember, world.TeamId, "glpat-fresh").ConfigureAwait(false);
        var notThisTeams = await ReplaceTokenAsync(elsewhere.CredentialId, world.MayaId, world.TeamId, "glpat-fresh").ConfigureAwait(false);
        var anonymous = await _tenants.SendAsync(HttpMethod.Put, $"/api/credentials/{world.CredentialId}/token", userId: null, world.TeamId, new { token = "glpat-fresh" }).ConfigureAwait(false);

        anotherAccount.StatusCode.ShouldBe(HttpStatusCode.Conflict, await TenantHarness.DescribeAsync(anotherAccount).ConfigureAwait(false));
        JsonDocument.Parse(await anotherAccount.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("code").GetString().ShouldBe("credential_account_mismatch");
        notWorking.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(notWorking).ConfigureAwait(false));
        (await notWorking.Content.ReadAsStringAsync().ConfigureAwait(false)).ShouldContain("Could not validate the token against GitLab");
        notYours.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(notYours).ConfigureAwait(false));
        notThisTeams.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(notThisTeams).ConfigureAwait(false));
        anonymous.StatusCode.ShouldBe(HttpStatusCode.Unauthorized, await TenantHarness.DescribeAsync(anonymous).ConfigureAwait(false));

        (await ReadTokenCredentialAsync(world.CredentialId).ConfigureAwait(false)).Token.ShouldBe(world.TokenOfCredential, "every refusal left the credential as it was");
        (await ReadTokenCredentialAsync(elsewhere.CredentialId).ConfigureAwait(false)).Token.ShouldBe(elsewhere.TokenOfCredential);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private sealed record World(Guid OwnerId, Guid MayaId, Guid TeamId, Guid InstanceId, Guid CredentialId, Guid RepositoryId, string TokenOfCredential);

    private sealed record StoredCredential(CredentialStatus Status, string? LastError, string EncryptedPayload, OAuthPayload StoredPair);

    private sealed record StoredToken(CredentialStatus Status, string? LastError, string Token);

    /// <summary>
    /// A team with an Admin (nacho) and a Member (maya), a GitLab instance whose OAuth app is the stub's, and a credential
    /// with a repository bound to it, the Error its dead token set already on it. <paramref name="tokenCredential"/> makes it
    /// maya's personal access token; otherwise nacho's OAuth pair — which GitLab never heard of when <paramref name="deadPair"/>.
    /// </summary>
    private async Task<World> SeedAsync(GitLabDoorkeeperStub gitlab, bool deadPair, bool tokenCredential = false)
    {
        var owner = await _tenants.AccountAsync().ConfigureAwait(false);
        var maya = await _tenants.AccountAsync().ConfigureAwait(false);
        var viewer = await _tenants.AccountAsync().ConfigureAwait(false);
        var team = await _tenants.TeamAsync((owner, TeamRole.Admin), (maya, TeamRole.Member), (viewer, TeamRole.Viewer)).ConfigureAwait(false);

        if (!deadPair && !tokenCredential) gitlab.Seed(GitLabDoorkeeperStub.DefaultClientId, "nacho", "at-0", "rt-0");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = $"gitlab-{Guid.NewGuid():N}"[..14], BaseUrl = gitlab.BaseUrl, ApiUrl = gitlab.BaseUrl, OauthClientId = GitLabDoorkeeperStub.DefaultClientId, OauthClientSecretEnc = encryptor.Encrypt(GitLabDoorkeeperStub.DefaultClientSecret), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        var ownerId = tokenCredential ? maya : owner;
        var token = $"glpat-expired-{Guid.NewGuid():N}";
        CredentialPayload payload = tokenCredential ? new PatPayload { Token = token } : new OAuthPayload { AccessToken = "at-0", RefreshToken = "rt-0", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, OwnerUserId = ownerId, Ownership = CredentialOwnership.Personal, AuthType = payload.Type, DisplayName = tokenCredential ? "GitLab · maya" : "GitLab · nacho", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)), ExpiresDate = (payload as OAuthPayload)?.ExpiresAt, Status = CredentialStatus.Error, LastError = "401 Unauthorized", CreatedBy = ownerId, LastModifiedBy = ownerId };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = Random.Shared.Next(100_000, 999_999).ToString(), NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{gitlab.BaseUrl}/acme/api", CloneUrlHttps = $"{gitlab.BaseUrl}/acme/api.git", Status = RepositoryStatus.Error, LastError = CredentialRevocationService.DisconnectedRepositoryError };
        var identity = new UserProviderIdentity { Id = Guid.NewGuid(), UserId = ownerId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ProviderUserId = gitlab.UserIdOf(tokenCredential ? "maya" : "nacho").ToString(), ProviderUsername = tokenCredential ? "maya" : "nacho" };

        db.ProviderInstance.Add(instance);
        db.Credential.Add(credential);
        db.Repository.Add(repository);
        db.UserProviderIdentity.Add(identity);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return new World(owner, maya, team, instance.Id, credential.Id, repository.Id, token);
    }

    private async Task<HttpResponseMessage> InitAsync(World world, string returnUrl, Guid replaceCredentialId) =>
        await _tenants.Client().SendAsync(_tenants.Request(HttpMethod.Post, "/api/credentials/oauth/init", world.OwnerId, world.TeamId, new { providerInstanceId = world.InstanceId, displayName = "GitLab · nacho", returnUrl, replaceCredentialId }));

    private async Task<HttpResponseMessage> CallbackAsync(string state, string code, string? bindingCookie)
    {
        var request = _tenants.Request(HttpMethod.Get, $"{CallbackPath}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", userId: null, teamId: null);

        if (bindingCookie != null) request.Headers.Add("Cookie", $"{BindingCookie}={bindingCookie}");

        return await _tenants.Client().SendAsync(request);
    }

    private async Task<HttpResponseMessage> CompleteAsync(Guid userId, Guid teamId, string code, string state) =>
        await _tenants.Client().SendAsync(_tenants.Request(HttpMethod.Post, CompletePath, userId, teamId, new { code, state }));

    private async Task<HttpResponseMessage> ReplaceTokenAsync(Guid credentialId, Guid userId, Guid teamId, string token) =>
        await _tenants.SendAsync(HttpMethod.Put, $"/api/credentials/{credentialId}/token", userId, teamId, new { token });

    /// <summary>The credential as <c>GET /api/credentials</c> says it — what the Providers list renders, with the health derived from the row.</summary>
    private async Task<JsonElement> ReadListedAsync(Guid userId, Guid teamId, Guid credentialId)
    {
        var response = await _tenants.SendAsync(HttpMethod.Get, "/api/credentials", userId, teamId).ConfigureAwait(false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response).ConfigureAwait(false));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return json.RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == credentialId).Clone();
    }

    private async Task<StoredCredential> ReadCredentialAsync(Guid credentialId)
    {
        var credential = await _tenants.DbAsync(db => db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId)).ConfigureAwait(false);

        return new StoredCredential(credential.Status, credential.LastError, credential.EncryptedPayload, (OAuthPayload)Open(credential));
    }

    private async Task<StoredToken> ReadTokenCredentialAsync(Guid credentialId)
    {
        var credential = await _tenants.DbAsync(db => db.Credential.AsNoTracking().SingleAsync(c => c.Id == credentialId)).ConfigureAwait(false);

        return new StoredToken(credential.Status, credential.LastError, ((PatPayload)Open(credential)).Token);
    }

    /// <summary>What the row holds, opened as stored — not through <see cref="ICredentialResolver"/>, which refuses a credential its provider refused (Error), and a refused re-authorization leaves exactly that.</summary>
    private CredentialPayload Open(Credential credential)
    {
        using var scope = _factory.Services.CreateScope();

        return scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>().Deserialize(credential.AuthType, scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Decrypt(credential.EncryptedPayload));
    }

    private void Dump(GitLabDoorkeeperStub gitlab)
    {
        _output.WriteLine($"GitLab saw: rotated={gitlab.Count(StubEvent.Rotated)} refused={gitlab.Count(StubEvent.Refused)} authorized={gitlab.Count(StubEvent.Authorized)} revoked={gitlab.Count(StubEvent.Revoked)} api-200={gitlab.Count(StubEvent.Api200)} api-401={gitlab.Count(StubEvent.Api401)}");

        foreach (var entry in gitlab.Log) _output.WriteLine($"  {entry.At:HH:mm:ss.fff} {entry.Kind}: {entry.Detail}");
    }

    private static async Task<string> StateOfAsync(HttpResponseMessage init) =>
        JsonDocument.Parse(await init.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString()!;

    private static string? BindingCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(c => c.StartsWith($"{BindingCookie}=", StringComparison.Ordinal)) : null;

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0][(BindingCookie.Length + 1)..];
}

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

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity — the person a provider credential belongs to reconnects it themselves, through the real HTTP surface: the real
/// ASP.NET pipeline (routing, JWT, the <c>X-Team-Id</c> scope, the authorization behaviors, the exception filter), cookies on the wire,
/// the real GitLab OAuth client and provider, real Postgres; the one fake is GitLab, a <see cref="GitLabDoorkeeperStub"/> on a loopback port.
///
/// <para>The rule under test: <c>POST /api/credentials/oauth/init</c> naming a <c>replaceCredentialId</c> takes <c>credentials.manage</c> of
/// anyone but the credential's own person, who passes on membership alone — the bar <c>PUT /api/credentials/{id}/token</c> applies. So a
/// plain Member (or a Viewer) whose sign-in died brings it back in place, in the browser that started it and across sites through the
/// SPA's completion page; a teammate's credential still takes an Admin; and a connect that names no credential still takes
/// <c>credentials.manage</c> of everyone. The credential-side guards — the same provider account, a retired credential, a departed owner
/// — are those of <see cref="CredentialReauthorizationE2ETests"/> and stay in force.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "ProviderE2E")]
[Trait("Surface", "Http")]
public sealed class CredentialOwnerReconnectE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string BindingCookie = "codespace_oauth_nonce";
    private const string CallbackPath = "/api/credentials/oauth/callback";
    private const string CompletePath = "/api/credentials/oauth/complete";

    /// <summary>A dev SPA origin on another site than the API's http://localhost — on the CORS list in Development.</summary>
    private const string CrossSiteSpa = "http://127.0.0.1:5180";

    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public CredentialOwnerReconnectE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public async Task The_credentials_own_person_reconnects_in_the_browser_that_started_it_whatever_their_role(TeamRole role)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var (ownerId, account) = world.PersonOf(role);
        var credentialId = world.CredentialOf(ownerId);

        var init = await InitAsync(world, ownerId, credentialId, "/settings/credentials").ConfigureAwait(false);

        init.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(init).ConfigureAwait(false));
        var callback = await CallbackAsync(await StateOfAsync(init).ConfigureAwait(false), $"authz:{account}", CookieValue(BindingCookieOf(init)!)).ConfigureAwait(false);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback).ConfigureAwait(false));
        callback.Headers.Location!.OriginalString.ShouldBe($"/settings/credentials?oauthCredentialId={credentialId}", "the popup is told the credential it already had");

        var credential = await ReadCredentialAsync(credentialId).ConfigureAwait(false);
        credential.Status.ShouldBe(CredentialStatus.Active);
        credential.LastError.ShouldBeNull();
        credential.StoredPair.RefreshToken.ShouldBe(gitlab.LiveRefreshTokenOf(account), "the row holds the pair GitLab issued for this sign-in");
        (await _tenants.DbAsync(db => db.Credential.AsNoTracking().CountAsync(c => c.TeamId == world.TeamId)).ConfigureAwait(false)).ShouldBe(world.CredentialCount, "no second credential was minted");

        var repository = await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.CredentialId == credentialId)).ConfigureAwait(false);
        repository.Status.ShouldBe(RepositoryStatus.Active, "the Error its dead token had set is lifted, and it is still bound to the same credential");

        var listed = await ReadListedAsync(ownerId, world.TeamId, credentialId).ConfigureAwait(false);
        listed.GetProperty("health").GetString().ShouldBe("active");
    }

    [Theory]
    [InlineData(TeamRole.Member)]
    [InlineData(TeamRole.Viewer)]
    public async Task A_cross_site_reconnect_by_the_credentials_own_person_is_finished_by_their_spa(TeamRole role)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var (ownerId, account) = world.PersonOf(role);
        var credentialId = world.CredentialOf(ownerId);
        var state = await StateOfAsync(await InitAsync(world, ownerId, credentialId, $"{CrossSiteSpa}/oauth-callback").ConfigureAwait(false)).ConfigureAwait(false);

        // The SPA and the API are on different sites, so the binding cookie never comes back: the callback hands the code to the SPA
        // origin the connect named, exchanging nothing.
        var callback = await CallbackAsync(state, $"authz:{account}", bindingCookie: null).ConfigureAwait(false);

        callback.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(callback).ConfigureAwait(false));
        callback.Headers.Location!.OriginalString.ShouldStartWith($"{CrossSiteSpa}/oauth-callback#oauthCode=");
        gitlab.Count(StubEvent.Authorized).ShouldBe(0, "handing off exchanges nothing");

        var finish = await CompleteAsync(ownerId, world.TeamId, $"authz:{account}", state).ConfigureAwait(false);

        finish.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(finish).ConfigureAwait(false));
        JsonDocument.Parse(await finish.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("credentialId").GetGuid().ShouldBe(credentialId);
        (await ReadCredentialAsync(credentialId).ConfigureAwait(false)).StoredPair.RefreshToken.ShouldBe(gitlab.LiveRefreshTokenOf(account));
        (await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.State == state)).ConfigureAwait(false)).ShouldBe(0, "finishing spends the state");
    }

    [Fact]
    public async Task A_member_cannot_reconnect_a_teammates_credential_and_an_admin_can_start_to()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var mayas = world.CredentialOf(world.MayaId);

        var byAnotherMember = await InitAsync(world, world.MiloId, mayas, "/settings/credentials").ConfigureAwait(false);
        var byAViewer = await InitAsync(world, world.ViewerId, mayas, "/settings/credentials").ConfigureAwait(false);
        var byAnAdmin = await InitAsync(world, world.BossId, mayas, "/settings/credentials").ConfigureAwait(false);

        byAnotherMember.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(byAnotherMember).ConfigureAwait(false));
        byAViewer.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(byAViewer).ConfigureAwait(false));
        (await byAnotherMember.Content.ReadAsStringAsync().ConfigureAwait(false)).ShouldContain("Your role on this team is Member", Case.Sensitive, "the refusal says what the person can act on: their own role");
        byAnAdmin.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(byAnAdmin).ConfigureAwait(false));
        (await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.TeamId == world.TeamId)).ConfigureAwait(false)).ShouldBe(1, "only the Admin's start left a pending connect");
    }

    [Fact]
    public async Task An_admin_finishes_a_members_reconnect_only_by_signing_in_as_that_members_account()
    {
        // credentials.manage still reaches every credential below the Admin: the start is theirs, and the account that signs in must be Maya's.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var mayas = world.CredentialOf(world.MayaId);
        var before = await ReadCredentialAsync(mayas).ConfigureAwait(false);

        var asTheAdmin = await InitAsync(world, world.BossId, mayas, "/settings/credentials").ConfigureAwait(false);
        var refused = await CallbackAsync(await StateOfAsync(asTheAdmin).ConfigureAwait(false), "authz:boss", CookieValue(BindingCookieOf(asTheAdmin)!)).ConfigureAwait(false);

        refused.StatusCode.ShouldBe(HttpStatusCode.Conflict, await TenantHarness.DescribeAsync(refused).ConfigureAwait(false));
        (await ReadCredentialAsync(mayas).ConfigureAwait(false)).EncryptedPayload.ShouldBe(before.EncryptedPayload, "the Admin's own account is not Maya's");

        var again = await InitAsync(world, world.BossId, mayas, "/settings/credentials").ConfigureAwait(false);
        var finished = await CallbackAsync(await StateOfAsync(again).ConfigureAwait(false), "authz:maya", CookieValue(BindingCookieOf(again)!)).ConfigureAwait(false);

        finished.StatusCode.ShouldBe(HttpStatusCode.Redirect, await TenantHarness.DescribeAsync(finished).ConfigureAwait(false));
        (await ReadCredentialAsync(mayas).ConfigureAwait(false)).StoredPair.RefreshToken.ShouldBe(gitlab.LiveRefreshTokenOf("maya"));
    }

    [Fact]
    public async Task An_admin_cannot_reconnect_an_owners_or_a_peer_admins_sign_in_and_the_list_never_offers_it()
    {
        // credentials.manage is the permission, and a reconnect signs in AS the person, so it also takes the reach over them: an Admin reaches the
        // Members below, never another Admin or an Owner. What the list says (callerMayRepair — what the repository rows' Reconnect is shown by)
        // is the answer the endpoint then gives, asker by asker.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);

        var cases = new (Guid Asker, string AskerLabel, Guid Person, string PersonLabel, bool Accepted)[]
        {
            (world.BossId, "an Admin", world.MayaId, "a Member's", true),
            (world.BossId, "an Admin", world.ViewerId, "a Viewer's", true),
            (world.BossId, "an Admin", world.AdaId, "a peer Admin's", false),
            (world.BossId, "an Admin", world.OliveId, "an Owner's", false),
            (world.OliveId, "an Owner", world.AdaId, "an Admin's", true),
            (world.OliveId, "an Owner", world.MayaId, "a Member's", true),
            (world.OliveId, "an Owner", world.OliveId, "their own", true),
            (world.AdaId, "an Admin", world.AdaId, "their own", true),
            (world.MayaId, "a Member", world.MayaId, "their own", true),
            (world.MayaId, "a Member", world.ViewerId, "a Viewer's", false),
            (world.MiloId, "another Member", world.MayaId, "a Member's", false),
        };

        foreach (var (asker, askerLabel, person, personLabel, accepted) in cases)
        {
            var credentialId = world.CredentialOf(person);
            var init = await InitAsync(world, asker, credentialId, "/settings/credentials").ConfigureAwait(false);
            var listed = await ReadListedAsync(asker, world.TeamId, credentialId).ConfigureAwait(false);

            init.StatusCode.ShouldBe(accepted ? HttpStatusCode.OK : HttpStatusCode.Forbidden, $"{askerLabel} reconnecting {personLabel} sign-in: {await TenantHarness.DescribeAsync(init).ConfigureAwait(false)}");
            listed.GetProperty("callerMayRepair").GetBoolean().ShouldBe(accepted, $"the list must say what the endpoint says to {askerLabel} about {personLabel} sign-in");
        }
    }

    [Fact]
    public async Task A_credential_whose_person_left_is_offered_to_nobody_and_the_endpoint_refuses_it_while_it_waits_to_be_retired()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var mayas = world.CredentialOf(world.MayaId);

        await _tenants.DbAsync(async db =>
        {
            await db.TeamMembership.Where(m => m.TeamId == world.TeamId && m.UserId == world.MayaId).ExecuteDeleteAsync();
            return 0;
        }).ConfigureAwait(false);

        var byAnOwner = await InitAsync(world, world.OliveId, mayas, "/settings/credentials").ConfigureAwait(false);

        byAnOwner.StatusCode.ShouldBe(HttpStatusCode.NotFound, await TenantHarness.DescribeAsync(byAnOwner).ConfigureAwait(false));
        (await ReadListedAsync(world.OliveId, world.TeamId, mayas).ConfigureAwait(false)).GetProperty("callerMayRepair").GetBoolean().ShouldBeFalse("there is no one left to sign in as, so no button is offered to anyone");
        (await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.TeamId == world.TeamId)).ConfigureAwait(false)).ShouldBe(0);
    }

    [Fact]
    public async Task A_connect_that_names_no_credential_is_still_an_admins()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);

        foreach (var (userId, label, accepted) in new[] { (world.MayaId, "a member", false), (world.ViewerId, "a viewer", false), (world.BossId, "an admin", true) })
        {
            var response = await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/oauth/init", userId, world.TeamId, new { providerInstanceId = world.InstanceId, displayName = "GitLab", returnUrl = "/settings/credentials" }).ConfigureAwait(false);

            response.StatusCode.ShouldBe(accepted ? HttpStatusCode.OK : HttpStatusCode.Forbidden, $"{label}: {await TenantHarness.DescribeAsync(response).ConfigureAwait(false)}");
        }
    }

    [Fact]
    public async Task A_person_who_left_the_team_cannot_reconnect_and_their_retired_credential_stays_retired_for_everyone()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var mayas = world.CredentialOf(world.MayaId);

        // What a departure leaves: the membership gone, the token blanked, the row soft-deleted.
        await _tenants.DbAsync(async db =>
        {
            await db.TeamMembership.Where(m => m.TeamId == world.TeamId && m.UserId == world.MayaId).ExecuteDeleteAsync();
            await db.Credential.Where(c => c.Id == mayas).ExecuteUpdateAsync(s => s.SetProperty(c => c.EncryptedPayload, string.Empty).SetProperty(c => c.Status, CredentialStatus.Revoked).SetProperty(c => c.DeletedDate, DateTimeOffset.UtcNow));
            return 0;
        }).ConfigureAwait(false);

        var byTheLeaver = await InitAsync(world, world.MayaId, mayas, "/settings/credentials").ConfigureAwait(false);
        var byAnAdmin = await InitAsync(world, world.BossId, mayas, "/settings/credentials").ConfigureAwait(false);

        byTheLeaver.StatusCode.ShouldBe(HttpStatusCode.Forbidden, await TenantHarness.DescribeAsync(byTheLeaver).ConfigureAwait(false));
        byAnAdmin.StatusCode.ShouldBe(HttpStatusCode.Conflict, await TenantHarness.DescribeAsync(byAnAdmin).ConfigureAwait(false));
        JsonDocument.Parse(await byAnAdmin.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("code").GetString().ShouldBe("credential_retired");
        (await _tenants.DbAsync(db => db.OAuthPendingState.AsNoTracking().CountAsync(s => s.TeamId == world.TeamId)).ConfigureAwait(false)).ShouldBe(0);
    }

    [Fact]
    public async Task A_members_reconnect_as_another_provider_account_is_refused_with_its_own_code_and_writes_nothing()
    {
        // The owner's pass is a pass to re-authorize THEIR credential: the account that signs in must still be the one on record.
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var mayas = world.CredentialOf(world.MayaId);
        var before = await ReadCredentialAsync(mayas).ConfigureAwait(false);
        var init = await InitAsync(world, world.MayaId, mayas, "/settings/credentials").ConfigureAwait(false);

        var callback = await CallbackAsync(await StateOfAsync(init).ConfigureAwait(false), "authz:someone-else", CookieValue(BindingCookieOf(init)!)).ConfigureAwait(false);

        callback.StatusCode.ShouldBe(HttpStatusCode.Conflict, await TenantHarness.DescribeAsync(callback).ConfigureAwait(false));
        JsonDocument.Parse(await callback.Content.ReadAsStringAsync().ConfigureAwait(false)).RootElement.GetProperty("code").GetString().ShouldBe("credential_account_mismatch");
        (await ReadCredentialAsync(mayas).ConfigureAwait(false)).EncryptedPayload.ShouldBe(before.EncryptedPayload);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    /// <summary>A team of an Owner (olive), two Admins (boss, ada), two Members (maya, milo) and a Viewer (vera); a GitLab instance whose OAuth app is the stub's; a dead OAuth credential of maya's, vera's, olive's and ada's, each with a repository bound to it and the act-as identity that names it.</summary>
    private sealed record World(Guid TeamId, Guid InstanceId, Guid BossId, Guid MayaId, Guid MiloId, Guid ViewerId, Guid OliveId, Guid AdaId, IReadOnlyDictionary<Guid, Guid> Credentials)
    {
        public int CredentialCount => Credentials.Count;

        public Guid CredentialOf(Guid owner) => Credentials[owner];

        public (Guid UserId, string Account) PersonOf(TeamRole role) => role == TeamRole.Member ? (MayaId, "maya") : (ViewerId, "vera");
    }

    private sealed record StoredCredential(CredentialStatus Status, string? LastError, string EncryptedPayload, OAuthPayload StoredPair);

    private async Task<World> SeedAsync(GitLabDoorkeeperStub gitlab)
    {
        var boss = await _tenants.AccountAsync().ConfigureAwait(false);
        var maya = await _tenants.AccountAsync().ConfigureAwait(false);
        var milo = await _tenants.AccountAsync().ConfigureAwait(false);
        var vera = await _tenants.AccountAsync().ConfigureAwait(false);
        var olive = await _tenants.AccountAsync().ConfigureAwait(false);
        var ada = await _tenants.AccountAsync().ConfigureAwait(false);
        var team = await _tenants.TeamAsync((boss, TeamRole.Admin), (maya, TeamRole.Member), (milo, TeamRole.Member), (vera, TeamRole.Viewer), (olive, TeamRole.Owner), (ada, TeamRole.Admin)).ConfigureAwait(false);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = $"gitlab-{Guid.NewGuid():N}"[..14], BaseUrl = gitlab.BaseUrl, ApiUrl = gitlab.BaseUrl, OauthClientId = GitLabDoorkeeperStub.DefaultClientId, OauthClientSecretEnc = encryptor.Encrypt(GitLabDoorkeeperStub.DefaultClientSecret), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };
        var credentials = new Dictionary<Guid, Guid>();

        db.ProviderInstance.Add(instance);

        foreach (var (userId, account) in new[] { (maya, "maya"), (vera, "vera"), (olive, "olive"), (ada, "ada") })
        {
            // A pair GitLab never heard of: the dead grant the sign-in is there to replace.
            var payload = new OAuthPayload { AccessToken = $"at-{account}", RefreshToken = $"rt-{account}", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
            var credential = new Credential { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, OwnerUserId = userId, Ownership = CredentialOwnership.Personal, AuthType = AuthType.OAuth, DisplayName = $"GitLab · {account}", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)), ExpiresDate = payload.ExpiresAt, Status = CredentialStatus.Error, LastError = "401 Unauthorized", CreatedBy = userId, LastModifiedBy = userId };

            db.Credential.Add(credential);
            db.Repository.Add(new Repository { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = Random.Shared.Next(100_000, 999_999).ToString(), NamespacePath = "acme", Name = $"api-{account}", FullPath = $"acme/api-{account}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{gitlab.BaseUrl}/acme/api-{account}", CloneUrlHttps = $"{gitlab.BaseUrl}/acme/api-{account}.git", Status = RepositoryStatus.Error, LastError = CredentialRevocationService.DisconnectedRepositoryError });
            db.UserProviderIdentity.Add(new UserProviderIdentity { Id = Guid.NewGuid(), UserId = userId, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ProviderUserId = gitlab.UserIdOf(account).ToString(), ProviderUsername = account });
            credentials[userId] = credential.Id;
        }

        await db.SaveChangesAsync().ConfigureAwait(false);

        return new World(team, instance.Id, boss, maya, milo, vera, olive, ada, credentials);
    }

    private async Task<HttpResponseMessage> InitAsync(World world, Guid userId, Guid replaceCredentialId, string returnUrl) =>
        await _tenants.Client().SendAsync(_tenants.Request(HttpMethod.Post, "/api/credentials/oauth/init", userId, world.TeamId, new { providerInstanceId = world.InstanceId, displayName = "GitLab", returnUrl, replaceCredentialId }));

    private async Task<HttpResponseMessage> CallbackAsync(string state, string code, string? bindingCookie)
    {
        var request = _tenants.Request(HttpMethod.Get, $"{CallbackPath}?code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(state)}", userId: null, teamId: null);

        if (bindingCookie != null) request.Headers.Add("Cookie", $"{BindingCookie}={bindingCookie}");

        return await _tenants.Client().SendAsync(request);
    }

    private async Task<HttpResponseMessage> CompleteAsync(Guid userId, Guid teamId, string code, string state) =>
        await _tenants.Client().SendAsync(_tenants.Request(HttpMethod.Post, CompletePath, userId, teamId, new { code, state }));

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

        using var scope = _factory.Services.CreateScope();
        var pair = credential.EncryptedPayload.Length == 0
            ? new OAuthPayload { AccessToken = string.Empty }
            : (OAuthPayload)scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>().Deserialize(credential.AuthType, scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Decrypt(credential.EncryptedPayload));

        return new StoredCredential(credential.Status, credential.LastError, credential.EncryptedPayload, pair);
    }

    private static async Task<string> StateOfAsync(HttpResponseMessage init) =>
        JsonDocument.Parse(await init.Content.ReadAsStringAsync()).RootElement.GetProperty("state").GetString()!;

    private static string? BindingCookieOf(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? values.FirstOrDefault(c => c.StartsWith($"{BindingCookie}=", StringComparison.Ordinal)) : null;

    private static string CookieValue(string setCookie) => setCookie.Split(';')[0][(BindingCookie.Length + 1)..];
}

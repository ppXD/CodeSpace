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
/// 🟢 High fidelity — a team that signs in to one GitLab through two OAuth applications holds two provider connections to that host, and a
/// repository bound under one through a credential that died is taken over by the live credential of the other: through the real HTTP surface (routing,
/// JWT, the <c>X-Team-Id</c> scope, the authorization behaviors, model binding, the exception filter), the real GitLab provider and
/// OAuth client, real Postgres, and a <see cref="GitLabDoorkeeperStub"/> on a loopback port as the one fake — it serves the project lookup the move
/// asks as the new credential, and is how the test sees that the lookup was made with the right token.
///
/// <para>What this pins end to end: the dialog's candidate list (<c>GET /api/provider-instances/{id}/relink-candidates</c>) offers the credential on the sibling
/// connection; <c>POST /api/repositories/{id}/relink-credential</c> and <c>POST /api/credentials/{id}/relink-bindings</c> move the repository to the
/// credential AND its connection — read back through the repository list under each connection — after asking GitLab for the project as that credential, and
/// refuse, writing nothing, when it cannot see the project or the credential is for another host; and what the older API said is still said: the
/// suggestions endpoint answers every field it did, and a relink within one connection is exactly what it was.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Category", "ProviderE2E")]
[Trait("Surface", "Http")]
public sealed class CredentialSameHostE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const long ProjectId = 4242;
    private const string ProjectPath = "acme/api";
    private const string SecondApplication = "app2";

    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public CredentialSameHostE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task The_dialogs_candidates_offer_the_callers_live_credential_on_the_other_connection_and_a_relink_takes_the_repository_there()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);

        var offered = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/provider-instances/{world.FirstConnection}/relink-candidates?fromCredentialId={world.DeadCredential}", world.BossId, world.TeamId).ConfigureAwait(false)).ConfigureAwait(false);

        offered.GetArrayLength().ShouldBe(1, "the boss's own live credential on the sibling connection — and nothing of a teammate's or of another host's");
        offered[0].GetProperty("credentialId").GetGuid().ShouldBe(world.BossCredentialOnSibling);
        offered[0].GetProperty("providerInstanceId").GetGuid().ShouldBe(world.SecondConnection);
        offered[0].GetProperty("kind").GetString().ShouldBe("YourConnection");

        var relink = await _tenants.SendAsync(HttpMethod.Post, $"/api/repositories/{world.Repository}/relink-credential", world.BossId, world.TeamId, new { newCredentialId = world.BossCredentialOnSibling }).ConfigureAwait(false);

        relink.StatusCode.ShouldBe(HttpStatusCode.NoContent, await TenantHarness.DescribeAsync(relink).ConfigureAwait(false));
        gitlab.Log.ShouldContain(e => e.Kind == StubEvent.Api200 && e.Token == "at-boss" && e.Detail.StartsWith($"/api/v4/projects/{ProjectId} "), "GitLab was asked for the project as the credential that takes it over");

        var repository = await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == world.Repository)).ConfigureAwait(false);

        (repository.CredentialId, repository.ProviderInstanceId, repository.Status, repository.LastError).ShouldBe((world.BossCredentialOnSibling, world.SecondConnection, RepositoryStatus.Active, null));
        (await ListedUnderAsync(world, world.SecondConnection).ConfigureAwait(false)).ShouldBe(new[] { world.Repository }, "the repository list, as the SPA reads it, shows it under the connection it moved to");
        (await ListedUnderAsync(world, world.FirstConnection).ConfigureAwait(false)).ShouldBeEmpty();
        (await _tenants.DbAsync(db => db.RepositoryWebhook.AsNoTracking().Where(w => w.RepositoryId == world.Repository && w.RegistrationStatus != RepositoryWebhookRegistrationStatus.Cancelled).ToListAsync()).ConfigureAwait(false))
            .ShouldHaveSingleItem("one hook, registered through the new connection").RegistrationStatus.ShouldBe(RepositoryWebhookRegistrationStatus.Enqueued);
    }

    [Fact]
    public async Task Relinking_everything_a_dead_credential_held_does_the_same_for_every_repository_and_reports_it()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);

        var response = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.DeadCredential}/relink-bindings", world.BossId, world.TeamId, new { toCredentialId = world.BossCredentialOnSibling }).ConfigureAwait(false);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response).ConfigureAwait(false));
        (await ReadJsonAsync(response).ConfigureAwait(false)).GetProperty("movedRepositoryCount").GetInt32().ShouldBe(1);
        (await ListedUnderAsync(world, world.SecondConnection).ConfigureAwait(false)).ShouldBe(new[] { world.Repository });
        gitlab.Log.ShouldContain(e => e.Kind == StubEvent.Api200 && e.Token == "at-boss" && e.Detail.StartsWith($"/api/v4/projects/{ProjectId} "));
    }

    [Theory]
    [InlineData("relink-credential")]
    [InlineData("relink-bindings")]
    public async Task A_credential_that_cannot_see_the_project_refuses_the_move_and_writes_nothing(string door)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab, projectVisibleToTheNewCredential: false).ConfigureAwait(false);

        var response = await SendRelinkAsync(world, door, world.BossCredentialOnSibling).ConfigureAwait(false);

        ((int)response.StatusCode).ShouldBeInRange(400, 499, await TenantHarness.DescribeAsync(response).ConfigureAwait(false));
        (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).ShouldContain(ProjectPath, Case.Sensitive, "the refusal names what the credential cannot read");
        var repository = await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == world.Repository)).ConfigureAwait(false);

        (repository.CredentialId, repository.ProviderInstanceId, repository.Status).ShouldBe((world.DeadCredential, world.FirstConnection, RepositoryStatus.Error));
        (await ListedUnderAsync(world, world.FirstConnection).ConfigureAwait(false)).ShouldBe(new[] { world.Repository });
    }

    [Theory]
    [InlineData("relink-credential")]
    [InlineData("relink-bindings")]
    public async Task A_credential_of_another_host_is_refused_with_the_message_it_always_got(string door)
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);

        var response = await SendRelinkAsync(world, door, world.BossCredentialOnAnotherHost).ConfigureAwait(false);

        ((int)response.StatusCode).ShouldBeInRange(400, 499, await TenantHarness.DescribeAsync(response).ConfigureAwait(false));
        (await response.Content.ReadAsStringAsync().ConfigureAwait(false)).ShouldContain("is for a different provider instance", Case.Sensitive);
        (await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == world.Repository)).ConfigureAwait(false)).CredentialId.ShouldBe(world.DeadCredential);
        gitlab.Log.ShouldNotContain(e => e.Kind == StubEvent.Api200 && e.Token == "at-elsewhere", "GitLab was never asked anything as a credential of another host");
    }

    [Fact]
    public async Task The_suggestions_endpoint_still_answers_every_field_the_older_dialog_reads_and_a_relink_within_one_connection_is_what_it_was()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var sameConnection = await SeedCredentialAsync(world.TeamId, world.FirstConnection, world.BossId, "boss-here", gitlab, "app1", "at-here").ConfigureAwait(false);

        var suggestions = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/credentials/{world.DeadCredential}/relink-suggestions", world.BossId, world.TeamId).ConfigureAwait(false)).ConfigureAwait(false);

        suggestions.EnumerateArray().Select(s => s.GetProperty("credentialId").GetGuid()).ShouldBe(new[] { sameConnection, world.BossCredentialOnSibling }, "the same connection's credential first; an older dialog ranks by this order and filters by connection itself");

        foreach (var suggestion in suggestions.EnumerateArray())
        {
            foreach (var field in new[] { "credentialId", "displayName", "authType", "ownership", "kind", "createdDate", "connectedBeforeDisconnect" }) suggestion.TryGetProperty(field, out _).ShouldBeTrue($"the field '{field}' an older dialog reads");

            suggestion.GetProperty("kind").GetString().ShouldBe("YourConnection");
        }

        var relink = await _tenants.SendAsync(HttpMethod.Post, $"/api/repositories/{world.Repository}/relink-credential", world.BossId, world.TeamId, new { newCredentialId = sameConnection }).ConfigureAwait(false);

        relink.StatusCode.ShouldBe(HttpStatusCode.NoContent, await TenantHarness.DescribeAsync(relink).ConfigureAwait(false));
        var repository = await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == world.Repository)).ConfigureAwait(false);

        (repository.CredentialId, repository.ProviderInstanceId, repository.Status).ShouldBe((sameConnection, world.FirstConnection, RepositoryStatus.Active));
        gitlab.Log.ShouldNotContain(e => e.Detail.StartsWith($"/api/v4/projects/{ProjectId} "), "a relink within one connection asks GitLab nothing, as it never did");
    }

    [Fact]
    public async Task A_disconnect_that_names_the_credential_on_the_other_connection_takes_the_repository_there()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var leaving = await SeedCredentialAsync(world.TeamId, world.FirstConnection, world.BossId, "boss-leaving", gitlab, "app1", "at-leaving").ConfigureAwait(false);

        await _tenants.DbAsync(async db =>
        {
            await db.Repository.Where(r => r.Id == world.Repository).ExecuteUpdateAsync(s => s.SetProperty(r => r.CredentialId, leaving).SetProperty(r => r.Status, RepositoryStatus.Active).SetProperty(r => r.LastError, (string?)null));
            return 0;
        }).ConfigureAwait(false);

        var revoke = await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{leaving}/revoke", world.BossId, world.TeamId, new { moveBindingsToCredentialId = world.BossCredentialOnSibling }).ConfigureAwait(false);

        revoke.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(revoke).ConfigureAwait(false));
        (await ReadJsonAsync(revoke).ConfigureAwait(false)).GetProperty("movedRepositoryCount").GetInt32().ShouldBe(1);
        (await ListedUnderAsync(world, world.SecondConnection).ConfigureAwait(false)).ShouldBe(new[] { world.Repository });
    }

    [Fact]
    public async Task A_candidate_on_the_other_connection_names_the_teammates_who_act_as_themselves_only_on_the_connection_the_repository_leaves()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var mayaHere = await SeedCredentialAsync(world.TeamId, world.FirstConnection, world.MayaId, "maya-here", gitlab, "app1", "at-maya").ConfigureAwait(false);
        var sameConnection = await SeedCredentialAsync(world.TeamId, world.FirstConnection, world.BossId, "boss-here", gitlab, "app1", "at-here").ConfigureAwait(false);

        await _tenants.DbAsync(async db =>
        {
            db.UserProviderIdentity.Add(new UserProviderIdentity { Id = Guid.NewGuid(), UserId = world.MayaId, ProviderInstanceId = world.FirstConnection, CredentialId = mayaHere, ProviderUserId = "gl-maya", ProviderUsername = "maya" });
            await db.SaveChangesAsync();

            return 0;
        }).ConfigureAwait(false);

        var mayaName = await _tenants.DbAsync(db => db.User.AsNoTracking().Where(u => u.Id == world.MayaId).Select(u => u.Name).SingleAsync()).ConfigureAwait(false);
        var offered = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/provider-instances/{world.FirstConnection}/relink-candidates?fromCredentialId={world.DeadCredential}", world.BossId, world.TeamId).ConfigureAwait(false)).ConfigureAwait(false);

        string[] StrandedBy(Guid credentialId) => offered.EnumerateArray().Single(c => c.GetProperty("credentialId").GetGuid() == credentialId).GetProperty("strandedActAs").EnumerateArray().Select(n => n.GetString()!).ToArray();

        StrandedBy(world.BossCredentialOnSibling).ShouldBe(new[] { mayaName }, "Maya acts as herself on the connection the repository would leave, and has no identity on the one it would go to");
        StrandedBy(sameConnection).ShouldBeEmpty("a credential on the repository's own connection strands nobody");
    }

    [Fact]
    public async Task A_static_token_past_its_expiry_is_listed_as_rejected_and_is_not_offered_to_re_link_to()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);
        var expired = await SeedTokenAsync(world, "expired-pat", DateTimeOffset.UtcNow.AddDays(-3), world.FirstConnection).ConfigureAwait(false);
        var healthy = await SeedTokenAsync(world, "healthy-pat", DateTimeOffset.UtcNow.AddDays(90), world.FirstConnection).ConfigureAwait(false);

        var listed = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, "/api/credentials", world.BossId, world.TeamId).ConfigureAwait(false)).ConfigureAwait(false);
        var offered = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, $"/api/provider-instances/{world.FirstConnection}/relink-candidates?fromCredentialId={world.DeadCredential}", world.BossId, world.TeamId).ConfigureAwait(false)).ConfigureAwait(false);

        string Row(Guid id, string field) => listed.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == id).GetProperty(field).GetString()!;

        (Row(expired, "status"), Row(expired, "health")).ShouldBe(("Active", "rejected"), "the list already reads it as rejected while its status still says Active");
        Row(healthy, "health").ShouldBe("active");

        var offeredIds = offered.EnumerateArray().Select(c => c.GetProperty("credentialId").GetGuid()).ToArray();

        offeredIds.ShouldContain(healthy);
        offeredIds.ShouldNotContain(expired, "the dialog does not offer what the list calls rejected");
    }

    [Fact]
    public async Task A_route_this_server_does_not_have_answers_404_with_a_not_found_body_which_is_how_the_dialog_knows_an_older_server_from_a_broken_one()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedAsync(gitlab).ConfigureAwait(false);

        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/provider-instances/{world.FirstConnection}/relink-candidates-this-server-does-not-have", world.BossId, world.TeamId).ConfigureAwait(false);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType?.MediaType.ShouldBe("application/json");

        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        body.RootElement.GetProperty("code").GetString().ShouldBe("not_found", "the SPA's fallback to its own filter reads any 404 from the candidates route; the body here is what a real older server sends");
        body.RootElement.GetProperty("message").GetString()!.ShouldContain("This server has no", Case.Sensitive);
    }

    // ─── Plumbing ──────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid BossId, Guid MayaId, Guid FirstConnection, Guid SecondConnection, Guid DeadCredential, Guid BossCredentialOnSibling, Guid BossCredentialOnAnotherHost, Guid Repository);

    /// <summary>
    /// A team of an Admin (boss), a Member (maya) and a Viewer, and one GitLab — the stub — reached through two connections (two OAuth applications), plus a
    /// connection to another host. Maya's credential on the first is dead (disconnected) with a repository bound through it, which the stub knows as project
    /// 4242; boss holds a live OAuth credential on the second, the stub's other application, and one on the other host.
    /// </summary>
    private async Task<World> SeedAsync(GitLabDoorkeeperStub gitlab, bool projectVisibleToTheNewCredential = true)
    {
        gitlab.AddApplication(SecondApplication, "secret2");
        gitlab.AddProject(projectVisibleToTheNewCredential ? ProjectId : ProjectId + 1, ProjectPath);

        var boss = await _tenants.AccountAsync().ConfigureAwait(false);
        var maya = await _tenants.AccountAsync().ConfigureAwait(false);
        var vera = await _tenants.AccountAsync().ConfigureAwait(false);
        var team = await _tenants.TeamAsync((boss, TeamRole.Admin), (maya, TeamRole.Member), (vera, TeamRole.Viewer)).ConfigureAwait(false);

        Guid first, second, elsewhere;

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
            var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();

            ProviderInstance Connection(string name, string baseUrl, string clientId, string secret) => new() { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = $"{name}-{Guid.NewGuid():N}"[..14], BaseUrl = baseUrl, ApiUrl = baseUrl, OauthClientId = clientId, OauthClientSecretEnc = encryptor.Encrypt(secret), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

            var one = Connection("GitLab", gitlab.BaseUrl, GitLabDoorkeeperStub.DefaultClientId, GitLabDoorkeeperStub.DefaultClientSecret);
            var two = Connection("GitLab-TED", gitlab.BaseUrl, SecondApplication, "secret2");
            var other = Connection("Elsewhere", $"https://gitlab-{Guid.NewGuid():N}.test", "app3", "secret3");

            db.ProviderInstance.AddRange(one, two, other);
            await db.SaveChangesAsync().ConfigureAwait(false);

            (first, second, elsewhere) = (one.Id, two.Id, other.Id);
        }

        var dead = await SeedDeadCredentialAsync(team, first, maya).ConfigureAwait(false);
        var bossOnSibling = await SeedCredentialAsync(team, second, boss, "boss-sibling", gitlab, SecondApplication, "at-boss").ConfigureAwait(false);
        var bossElsewhere = await SeedCredentialAsync(team, elsewhere, boss, "boss-elsewhere", gitlab, "app1", "at-elsewhere").ConfigureAwait(false);
        var repository = await SeedRepositoryAsync(team, first, dead, gitlab).ConfigureAwait(false);

        return new World(team, boss, maya, first, second, dead, bossOnSibling, bossElsewhere, repository);
    }

    /// <summary>What a Disconnect leaves: the row revoked and the token blanked.</summary>
    private async Task<Guid> SeedDeadCredentialAsync(Guid team, Guid connection, Guid owner) =>
        await _tenants.DbAsync(async db =>
        {
            var credential = new Credential { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = connection, OwnerUserId = owner, Ownership = CredentialOwnership.Personal, AuthType = AuthType.OAuth, DisplayName = "GitLab · maya", EncryptedPayload = string.Empty, Status = CredentialStatus.Revoked, CreatedBy = owner, LastModifiedBy = owner };

            db.Credential.Add(credential);
            await db.SaveChangesAsync();

            return credential.Id;
        }).ConfigureAwait(false);

    /// <summary>A live OAuth credential whose pair the stub issued: <paramref name="accessToken"/> is live at GitLab for <paramref name="owner"/>'s account through <paramref name="application"/>.</summary>
    private async Task<Guid> SeedCredentialAsync(Guid team, Guid connection, Guid owner, string name, GitLabDoorkeeperStub gitlab, string application, string accessToken)
    {
        gitlab.Seed(application, name, accessToken, $"rt-{accessToken}");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var payload = new OAuthPayload { AccessToken = accessToken, RefreshToken = $"rt-{accessToken}", ExpiresAt = DateTimeOffset.UtcNow.AddHours(1) };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = connection, OwnerUserId = owner, Ownership = CredentialOwnership.Personal, AuthType = AuthType.OAuth, DisplayName = $"GitLab · {name}", EncryptedPayload = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Encrypt(scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>().Serialize(payload)), ExpiresDate = payload.ExpiresAt, Status = CredentialStatus.Active, CreatedBy = owner, LastModifiedBy = owner };

        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }

    /// <summary>A boss-owned personal access token that is still Active by status, recorded as expiring at <paramref name="expiresAt"/>.</summary>
    private async Task<Guid> SeedTokenAsync(World world, string name, DateTimeOffset expiresAt, Guid connection)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var sealedToken = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Encrypt(scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = $"glpat-{name}" }));
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = connection, OwnerUserId = world.BossId, Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = name, EncryptedPayload = sealedToken, ExpiresDate = expiresAt, Status = CredentialStatus.Active, CreatedBy = world.BossId, LastModifiedBy = world.BossId };

        db.Credential.Add(credential);
        await db.SaveChangesAsync().ConfigureAwait(false);

        return credential.Id;
    }

    private async Task<Guid> SeedRepositoryAsync(Guid team, Guid connection, Guid credential, GitLabDoorkeeperStub gitlab) =>
        await _tenants.DbAsync(async db =>
        {
            var repository = new Repository { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = connection, CredentialId = credential, ExternalId = ProjectId.ToString(), NamespacePath = "acme", Name = "api", FullPath = ProjectPath, DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{gitlab.BaseUrl}/{ProjectPath}", CloneUrlHttps = $"{gitlab.BaseUrl}/{ProjectPath}.git", Status = RepositoryStatus.Error, LastError = CredentialRevocationService.DisconnectedRepositoryError };

            db.Repository.Add(repository);
            await db.SaveChangesAsync();

            return repository.Id;
        }).ConfigureAwait(false);

    private async Task<HttpResponseMessage> SendRelinkAsync(World world, string door, Guid toCredential) => door == "relink-credential"
        ? await _tenants.SendAsync(HttpMethod.Post, $"/api/repositories/{world.Repository}/relink-credential", world.BossId, world.TeamId, new { newCredentialId = toCredential }).ConfigureAwait(false)
        : await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{world.DeadCredential}/relink-bindings", world.BossId, world.TeamId, new { toCredentialId = toCredential }).ConfigureAwait(false);

    /// <summary>The ids <c>GET /api/repositories?providerInstanceId=…</c> lists — what the SPA's repository list shows under a connection.</summary>
    private async Task<Guid[]> ListedUnderAsync(World world, Guid connection)
    {
        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories?providerInstanceId={connection}", world.BossId, world.TeamId).ConfigureAwait(false);

        return (await ReadJsonAsync(response).ConfigureAwait(false)).EnumerateArray().Select(r => r.GetProperty("id").GetGuid()).ToArray();
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response).ConfigureAwait(false));

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync().ConfigureAwait(false));

        return json.RootElement.Clone();
    }
}

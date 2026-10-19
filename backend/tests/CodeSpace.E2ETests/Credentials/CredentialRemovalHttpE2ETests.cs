using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// Clearing away the dead rows a Disconnect leaves behind, through the whole HTTP surface: <c>POST /credentials/{id}/revoke</c> makes the row, the credential
/// list says nothing leans on it (<c>dependentCount</c>), and <c>DELETE /credentials/{id}</c> removes it — or refuses, with a message the Providers dialog
/// shows as it is.
///
/// <para>Tier: 🟢 High-fidelity (Rule 12) — the real ASP.NET pipeline (routing, JWT auth, the <c>X-Team-Id</c> scope, the exception filter, the mediator and its
/// authorization behaviors) and real Postgres. A dead credential is made by the production Disconnect over HTTP, never by writing a Revoked row. One
/// documented seam, shared with <see cref="CredentialBindingsHttpE2ETests"/>: the provider is the Git test provider, so nothing real is contacted.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class CredentialRemovalHttpE2ETests : IClassFixture<CredentialBindingsHttpE2ETests.GitProviderApiFactory>
{
    private readonly CredentialBindingsHttpE2ETests.GitProviderApiFactory _factory;
    private readonly TenantHarness _tenants;

    public CredentialRemovalHttpE2ETests(CredentialBindingsHttpE2ETests.GitProviderApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task A_member_removes_their_own_disconnected_credential_and_it_leaves_the_list()
    {
        var world = await SeedWorldAsync();
        var token = await SeedCredentialAsync(world, world.MemberId, "pat-dead");
        var keeper = await SeedCredentialAsync(world, world.MemberId, "pat-keeper");

        (await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{token}/revoke", world.MemberId, world.TeamId)).IsSuccessStatusCode.ShouldBeTrue("fixture check: disconnected through the door");

        var listed = await ListAsync(world, world.MemberId);

        listed[token].GetProperty("status").GetString().ShouldBe("Revoked");
        listed[token].GetProperty("dependentCount").GetInt32().ShouldBe(0, "the list says it is history");

        var response = await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{token}", world.MemberId, world.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));
        (await ReadJsonAsync(response)).GetProperty("credentialId").GetGuid().ShouldBe(token);

        (await ListAsync(world, world.MemberId)).Keys.ToList().ShouldBe(new List<Guid> { keeper }, "gone from the list; the other credential is untouched");
        (await LoadCredentialAsync(token)).DeletedDate.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_list_says_what_leans_on_each_credential_and_keeps_every_field_it_always_had()
    {
        var world = await SeedWorldAsync();
        var dead = await SeedCredentialAsync(world, world.MemberId, "pat-dead");
        var quiet = await SeedCredentialAsync(world, world.MemberId, "pat-quiet");
        var live = await SeedCredentialAsync(world, world.MemberId, "pat-live");

        await SeedRepositoryAsync(world, dead);
        await SeedRepositoryAsync(world, live);

        foreach (var id in new[] { dead, quiet }) (await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{id}/revoke", world.MemberId, world.TeamId)).IsSuccessStatusCode.ShouldBeTrue();

        var listed = await ListAsync(world, world.MemberId);

        listed[quiet].GetProperty("dependentCount").GetInt32().ShouldBe(0);
        listed[live].GetProperty("dependentCount").GetInt32().ShouldBe(1);
        listed[dead].GetProperty("boundRepositoryCount").GetInt32().ShouldBe(listed[dead].GetProperty("dependentCount").GetInt32(), "with no hooks or identities, the dependents are the bound repositories");

        foreach (var field in new[] { "boundConnectionWebhookCount", "actAsIdentityCount" })
            listed[dead].GetProperty(field).GetInt32().ShouldBe(0, $"'{field}' is sent for every row, and nothing but the repository leans on this one");

        foreach (var field in new[] { "id", "teamId", "providerInstanceId", "ownerUserId", "ownership", "authType", "displayName", "status", "lastValidatedDate", "createdDate", "health", "boundRepositoryCount", "callerMayRepair" })
            listed[live].TryGetProperty(field, out _).ShouldBeTrue($"an older client reads '{field}', which must still be there");
    }

    [Fact]
    public async Task A_credential_that_is_still_connected_is_a_400_that_says_to_disconnect_it_first()
    {
        var world = await SeedWorldAsync();
        var live = await SeedCredentialAsync(world, world.MemberId, "pat-live");

        var response = await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{live}", world.MemberId, world.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(response));
        (await ReadJsonAsync(response, requireSuccess: false)).GetProperty("message").GetString().ShouldContain("Disconnect it first");
        (await LoadCredentialAsync(live)).DeletedDate.ShouldBeNull();
    }

    [Fact]
    public async Task A_disconnected_credential_with_a_repository_left_on_it_is_a_400_that_says_which_and_what_to_do()
    {
        var world = await SeedWorldAsync();
        var dead = await SeedCredentialAsync(world, world.MemberId, "pat-dead");
        var repoId = await SeedRepositoryAsync(world, dead);

        (await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{dead}/revoke", world.MemberId, world.TeamId)).IsSuccessStatusCode.ShouldBeTrue();

        var response = await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{dead}", world.MemberId, world.TeamId);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(response));

        var message = (await ReadJsonAsync(response, requireSuccess: false)).GetProperty("message").GetString();

        message.ShouldContain("1 repository");
        message.ShouldContain("Move or unlink them first");
        (await LoadCredentialAsync(dead)).DeletedDate.ShouldBeNull();
        (await _tenants.DbAsync(db => db.Repository.AsNoTracking().SingleAsync(r => r.Id == repoId))).CredentialId.ShouldBe(dead, "the repository was not touched");
    }

    [Fact]
    public async Task Only_whoever_manages_a_credential_removes_it_and_a_viewer_removes_only_their_own()
    {
        var world = await SeedWorldAsync();
        var viewer = await _tenants.AccountAsync();
        await _tenants.AddMemberAsync(world.TeamId, viewer, TeamRole.Viewer);

        var admins = await DisconnectedAsync(world, world.AdminId, "pat-admins");
        var members = await DisconnectedAsync(world, world.MemberId, "pat-members");
        var viewers = await DisconnectedAsync(world, viewer, "pat-viewers");

        (await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{admins}", world.MemberId, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a Member does not remove an Admin's");
        (await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{members}", viewer, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a Viewer does not remove a Member's");
        (await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{viewers}", viewer, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.OK, "a Viewer removes their own: it takes membership alone, like disconnecting it did");
        (await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{members}", world.AdminId, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.OK, "an Admin removes a teammate's");

        (await LoadCredentialAsync(admins)).DeletedDate.ShouldBeNull();
    }

    [Fact]
    public async Task Another_teams_credential_is_refused_whichever_team_is_named_and_removing_twice_is_refused_the_second_time()
    {
        var world = await SeedWorldAsync();
        var outsider = await _tenants.AccountAsync();
        var outsiderTeam = await _tenants.TeamAsync((outsider, TeamRole.Owner));
        var dead = await DisconnectedAsync(world, world.MemberId, "pat-dead");

        (await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{dead}", outsider, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "not a member of the credential's team");
        (await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{dead}", outsider, outsiderTeam)).StatusCode.ShouldBe(HttpStatusCode.Forbidden, "named through their own team: the credential is not theirs");
        (await LoadCredentialAsync(dead)).DeletedDate.ShouldBeNull();

        (await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{dead}", world.MemberId, world.TeamId)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await _tenants.SendAsync(HttpMethod.Delete, $"/api/credentials/{dead}", world.MemberId, world.TeamId)).IsSuccessStatusCode.ShouldBeFalse("it is gone");
    }

    // ─── Plumbing ────────────────────────────────────────────────────────────────

    private sealed record World(Guid TeamId, Guid OwnerId, Guid AdminId, Guid MemberId, Guid InstanceId);

    private async Task<World> SeedWorldAsync()
    {
        var owner = await _tenants.AccountAsync();
        var admin = await _tenants.AccountAsync();
        var member = await _tenants.AccountAsync();
        var teamId = await _tenants.TeamAsync((owner, TeamRole.Owner), (admin, TeamRole.Admin), (member, TeamRole.Member));

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "GitLab", BaseUrl = $"https://gitlab.removal.test/{suffix}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.ProviderInstance.Add(instance);
        await db.SaveChangesAsync();

        return new World(teamId, owner, admin, member, instance.Id);
    }

    /// <summary>A live personal token of <paramref name="ownerId"/>, written as a connect writes one (a person and an instance, sealed), then disconnected through the door.</summary>
    private async Task<Guid> DisconnectedAsync(World world, Guid ownerId, string token)
    {
        var id = await SeedCredentialAsync(world, ownerId, token);

        (await _tenants.SendAsync(HttpMethod.Post, $"/api/credentials/{id}/revoke", ownerId, world.TeamId)).IsSuccessStatusCode.ShouldBeTrue("fixture check: disconnected through the door");

        return id;
    }

    private async Task<Guid> SeedCredentialAsync(World world, Guid ownerId, string token)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var sealedToken = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Encrypt(scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>().Serialize(new PatPayload { Token = token }));

        var credential = new Credential { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = world.InstanceId, OwnerUserId = ownerId, Ownership = CredentialOwnership.Personal, AuthType = AuthType.Pat, DisplayName = token, EncryptedPayload = sealedToken, Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.Credential.Add(credential);
        await db.SaveChangesAsync();

        return credential.Id;
    }

    private async Task<Guid> SeedRepositoryAsync(World world, Guid credentialId)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = world.TeamId, ProviderInstanceId = world.InstanceId, CredentialId = credentialId, ExternalId = $"acme/ext-{suffix}", NamespacePath = "acme", Name = $"app-{suffix}", FullPath = $"acme/app-{suffix}", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "https://gitlab.removal.test", CloneUrlHttps = $"https://gitlab.removal.test/acme/app-{suffix}.git", Status = RepositoryStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId };

        db.Repository.Add(repository);
        await db.SaveChangesAsync();

        return repository.Id;
    }

    /// <summary>The credential list as the Providers dialog reads it, by credential id.</summary>
    private async Task<Dictionary<Guid, JsonElement>> ListAsync(World world, Guid userId)
    {
        var body = await ReadJsonAsync(await _tenants.SendAsync(HttpMethod.Get, "/api/credentials", userId, world.TeamId));

        return body.EnumerateArray().ToDictionary(c => c.GetProperty("id").GetGuid());
    }

    private Task<Credential> LoadCredentialAsync(Guid id) => _tenants.DbAsync(db => db.Credential.AsNoTracking().SingleAsync(c => c.Id == id));

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response, bool requireSuccess = true)
    {
        if (requireSuccess) response.IsSuccessStatusCode.ShouldBeTrue(await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<JsonElement>())!;
    }
}

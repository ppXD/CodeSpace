using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace CodeSpace.E2ETests.Repositories;

/// <summary>
/// A connected token is only ever presented to the host it was connected for. An Admin may edit a git provider
/// connection, but a PATCH that moves its base or API URL to another host while a member's token is connected is refused
/// — so a Viewer browsing a repository afterwards still reaches the real GitLab, and the host the Admin named is never
/// contacted at all. And the connection's URLs reach every member masked where they embed credentials.
///
/// <para>Driven through the real HTTP surface (PATCH /api/provider-instances, GET /api/repositories/{id}/branches,
/// GET /api/provider-instances), the real GitLab provider over NGitLab and the real GlobalExceptionFilter, against two
/// loopback GitLabs — the legitimate one the token was connected for and an attacker's.</para>
///
/// <para>Fidelity: HIGH. Only GitLab is a stub, and both are real sockets the production client would speak HTTP to.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ProviderInstanceRepointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public ProviderInstanceRepointE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Theory]
    [InlineData("baseUrl")]
    [InlineData("apiUrl")]
    public async Task An_admins_repoint_is_refused_and_the_owners_token_never_reaches_the_new_host(string field)
    {
        var token = $"glpat-REPOINT-CANARY-{Guid.NewGuid():N}";
        await using var legit = LoopbackResponder.Start(_ => LoopbackResponder.Ok("[]"));
        await using var attacker = LoopbackResponder.Start(_ => LoopbackResponder.Ok("[]"));
        var (admin, viewer, team, instanceId, repositoryId) = await SeedConnectedRepositoryAsync($"http://127.0.0.1:{legit.Port}", token);

        var repoint = await _tenants.SendAsync(HttpMethod.Patch, $"/api/provider-instances/{instanceId}", admin, team, Patch(field, $"http://127.0.0.1:{attacker.Port}"));

        repoint.StatusCode.ShouldBe(HttpStatusCode.PreconditionRequired, await TenantHarness.DescribeAsync(repoint));
        (await repoint.Content.ReadAsStringAsync()).ShouldContain("provider_endpoint_credentials_bound");

        var browse = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories/{repositoryId}/branches", viewer, team);

        browse.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(browse));
        legit.Requests.ShouldContain(head => head.Contains(token), "fixture check: the Viewer's read presented the owner's token to the host it was connected for");
        attacker.Accepted.ShouldBe(0, $"the refused {field} repoint stored nothing, so no request went to the Admin's host — check provider_instance.{field} if this fails");
    }

    [Fact]
    public async Task A_viewer_lists_the_connection_with_its_embedded_credentials_masked_and_a_masked_patch_keeps_them()
    {
        var canary = "CANARYPI" + Guid.NewGuid().ToString("N")[..12];
        var owner = await _tenants.AccountAsync();
        var viewer = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (viewer, TeamRole.Viewer));
        var instanceId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/provider-instances", owner, team, new { provider = "GitLab", displayName = "gitlab", baseUrl = $"https://svc:{canary}@gitlab.example", apiUrl = $"https://gitlab.example/api?private_token={canary}" }));

        var list = await _tenants.SendAsync(HttpMethod.Get, "/api/provider-instances", viewer, team);
        var body = await list.Content.ReadAsStringAsync();

        list.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldNotContain(canary, Case.Sensitive, "a Viewer read the credentials embedded in the connection's URLs");
        var listed = JsonDocument.Parse(body).RootElement.EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == instanceId);
        listed.GetProperty("baseUrl").GetString().ShouldBe("https://***:***@gitlab.example");
        listed.GetProperty("apiUrl").GetString().ShouldBe("https://gitlab.example/api?private_token=***");

        var patch = await _tenants.SendAsync(HttpMethod.Patch, $"/api/provider-instances/{instanceId}", owner, team, new { displayName = "renamed", baseUrl = listed.GetProperty("baseUrl").GetString(), apiUrl = listed.GetProperty("apiUrl").GetString() });

        patch.StatusCode.ShouldBe(HttpStatusCode.NoContent, await TenantHarness.DescribeAsync(patch));
        var stored = await _tenants.DbAsync(db => db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId));
        stored.BaseUrl.ShouldBe($"https://svc:{canary}@gitlab.example", "sending the masked form back means unchanged — the stored credentials are kept");
        stored.ApiUrl.ShouldBe($"https://gitlab.example/api?private_token={canary}");
        stored.DisplayName.ShouldBe("renamed");
    }

    private static object Patch(string field, string url) => field == "baseUrl" ? new { baseUrl = url } : new { apiUrl = url };

    /// <summary>The Owner connects GitLab and adds a PAT through the API, as an operator would; the repository row is seeded directly so no bind round-trip to the stub is needed.</summary>
    private async Task<(Guid Admin, Guid Viewer, Guid Team, Guid InstanceId, Guid RepositoryId)> SeedConnectedRepositoryAsync(string gitLabUrl, string token)
    {
        var owner = await _tenants.AccountAsync();
        var admin = await _tenants.AccountAsync();
        var viewer = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (admin, TeamRole.Admin), (viewer, TeamRole.Viewer));
        var instanceId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/provider-instances", owner, team, new { provider = "GitLab", displayName = "gitlab", baseUrl = gitLabUrl }));
        var credentialId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/pat", owner, team, new { providerInstanceId = instanceId, displayName = "owner pat", token }));
        var repositoryId = Guid.NewGuid();

        await _tenants.DbAsync(async db =>
        {
            db.Repository.Add(new Repository { Id = repositoryId, TeamId = team, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = "42", NamespacePath = "acme", Name = "web", FullPath = "acme/web", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "http://127.0.0.1/acme/web", Status = RepositoryStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            return await db.SaveChangesAsync();
        });

        return (admin, viewer, team, instanceId, repositoryId);
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}

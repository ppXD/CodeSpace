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
/// A GitLab base URL that redirects to another origin — http to https, a host alias, another port. The credential is only
/// ever sent to the origin it was connected for, so the redirect is not followed; what the owner sees is the typed
/// <c>provider_base_url_redirects</c> failure naming the URL GitLab pointed at, not a 401 that reads as a revoked token and
/// sends them to re-issue one that was never wrong. And the owner meets it where they type the URL: adding a provider whose
/// address redirects stores nothing.
///
/// <para>Driven through the real HTTP surface (POST /api/provider-instances, GET /api/repositories/{id}/branches), the real
/// GitLab provider over NGitLab, the real endpoint probe and the real GlobalExceptionFilter, against two loopback hosts: the
/// GitLab that redirects, and the origin it points at — which must never be contacted.</para>
///
/// <para>Fidelity: HIGH. Only GitLab is a stub, and both are real sockets the production client speaks HTTP to.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ProviderBaseUrlRedirectE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public ProviderBaseUrlRedirectE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Theory]
    [InlineData("http to https", "branches")]
    [InlineData("host alias", "branches")]
    [InlineData("other port", "tags")]
    [InlineData("other port", "pull-requests")]
    public async Task A_viewer_reads_where_gitlab_pointed_not_a_401_and_the_token_goes_nowhere_else(string shape, string page)
    {
        var token = $"glpat-REDIRECT-CANARY-{Guid.NewGuid():N}";
        await using var elsewhere = LoopbackResponder.Start(_ => LoopbackResponder.Ok("[]"));
        var destination = DestinationOf(shape, elsewhere.Port);
        var redirecting = false;
        await using var gitLab = LoopbackResponder.Start(head => redirecting ? LoopbackResponder.Redirect(destination + "/api/v4/x") : LoopbackResponder.Ok("[]"));
        var (viewer, _, team, instanceId, repositoryId) = await SeedConnectedRepositoryAsync($"http://127.0.0.1:{gitLab.Port}", token);

        redirecting = true;   // the address answered when it was added; it redirects now, as a host does once its proxy or external_url is changed
        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories/{repositoryId}/{page}", viewer, team);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        var failure = JsonDocument.Parse(body).RootElement;
        failure.GetProperty("code").GetString().ShouldBe("provider_base_url_redirects", body);
        body.ShouldContain(destination, Case.Sensitive, "the owner reads where GitLab pointed");
        body.ShouldNotContain(token, Case.Sensitive);
        elsewhere.Accepted.ShouldBe(0, $"the redirect to {shape} was not followed, so nothing — no credential — went to {destination}");
        gitLab.Requests.ShouldContain(head => head.Contains(token), "fixture check: the token was presented to the origin it was connected for");
        (await _tenants.DbAsync(db => db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId))).BaseUrl.ShouldBe($"http://127.0.0.1:{gitLab.Port}", "the stored base URL is never rewritten");
    }

    [Theory]
    [InlineData("http to https")]
    [InlineData("host alias")]
    [InlineData("other port")]
    public async Task Adding_a_provider_whose_address_redirects_is_refused_naming_where_it_points_and_stores_nothing(string shape)
    {
        await using var elsewhere = LoopbackResponder.Start(_ => LoopbackResponder.Ok("{}"));
        var destination = DestinationOf(shape, elsewhere.Port);
        await using var gitLab = LoopbackResponder.Start(_ => LoopbackResponder.Redirect(destination + "/api/v4/version"));
        var owner = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner));

        var add = await _tenants.SendAsync(HttpMethod.Post, "/api/provider-instances", owner, team, new { provider = "GitLab", displayName = "gitlab", baseUrl = $"http://127.0.0.1:{gitLab.Port}" });
        var body = await add.Content.ReadAsStringAsync();

        add.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("provider_base_url_redirects", body);
        body.ShouldContain(destination, Case.Sensitive);
        elsewhere.Accepted.ShouldBe(0, "the probe names the redirect, it never goes there");
        gitLab.Requests.ShouldHaveSingleItem().ShouldNotContain("PRIVATE-TOKEN", Case.Insensitive, "the probe sends no credential");
        (await _tenants.DbAsync(db => db.ProviderInstance.AsNoTracking().CountAsync(p => p.TeamId == team))).ShouldBe(0);
    }

    [Fact]
    public async Task An_https_upgrade_past_a_connected_token_is_refused_when_nothing_answers_over_tls_and_the_token_keeps_working()
    {
        // The self-service fix for a base URL that redirects to https is allowed past a connected token because it sends the
        // token nowhere new. It is not allowed to land the token on an address nothing serves over TLS: undoing that is itself
        // a refused move, so the owner would be stranded. The new address is asked first, and the stored one stays.
        var token = $"glpat-UPGRADE-CANARY-{Guid.NewGuid():N}";
        await using var gitLab = LoopbackResponder.Start(_ => LoopbackResponder.Ok("[]"));   // plain http on this port: a TLS handshake against it never completes
        var (viewer, admin, team, instanceId, repositoryId) = await SeedConnectedRepositoryAsync($"http://127.0.0.1:{gitLab.Port}", token);

        var upgrade = await _tenants.SendAsync(HttpMethod.Patch, $"/api/provider-instances/{instanceId}", admin, team, new { baseUrl = $"https://127.0.0.1:{gitLab.Port}" });
        var body = await upgrade.Content.ReadAsStringAsync();

        upgrade.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity, body);
        JsonDocument.Parse(body).RootElement.GetProperty("code").GetString().ShouldBe("provider_endpoint_unreachable", body);
        body.ShouldContain($"https://127.0.0.1:{gitLab.Port}", Case.Sensitive, "the owner reads which address did not answer");
        body.ShouldNotContain(token, Case.Sensitive);
        (await _tenants.DbAsync(db => db.ProviderInstance.AsNoTracking().SingleAsync(p => p.Id == instanceId))).BaseUrl.ShouldBe($"http://127.0.0.1:{gitLab.Port}", "the stored address is unchanged");

        var browse = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories/{repositoryId}/branches", viewer, team);

        browse.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(browse));
        gitLab.Requests.ShouldContain(head => head.Contains(token), "fixture check: the connected token still reaches the address it was connected for");
    }

    private static string DestinationOf(string shape, int port) => shape switch
    {
        "http to https" => $"https://127.0.0.1:{port}",
        "host alias" => $"http://localhost:{port}",
        "other port" => $"http://127.0.0.1:{port}",
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "an unknown redirect shape")
    };

    /// <summary>The Owner connects GitLab and adds a PAT through the API, as an operator would; the repository row is seeded directly so no bind round-trip to the stub is needed.</summary>
    private async Task<(Guid Viewer, Guid Admin, Guid Team, Guid InstanceId, Guid RepositoryId)> SeedConnectedRepositoryAsync(string gitLabUrl, string token)
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

        return (viewer, admin, team, instanceId, repositoryId);
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}

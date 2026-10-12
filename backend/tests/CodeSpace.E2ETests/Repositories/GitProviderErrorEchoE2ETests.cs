using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.E2ETests.Repositories;

/// <summary>
/// A GitLab (or a proxy in front of it) whose error body quotes the token it was presented must not hand that token to
/// a Viewer browsing the repository. Driven through the real HTTP surface, the real GitLab provider over NGitLab, the
/// real resilience translation and the real GlobalExceptionFilter, against a loopback GitLab that answers every project
/// call with a 404 echoing the request's <c>Authorization</c> and <c>PRIVATE-TOKEN</c>.
///
/// <para>Fidelity: HIGH. Only GitLab is a stub, and it is a real socket the production client speaks HTTP to.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class GitProviderErrorEchoE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public GitProviderErrorEchoE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Theory]
    [InlineData("branches")]
    [InlineData("tags")]
    [InlineData("releases")]
    [InlineData("pull-requests")]
    [InlineData("issues")]
    [InlineData("tree")]
    public async Task A_viewer_never_reads_the_token_an_echoing_gitlab_quoted(string page)
    {
        var token = $"glpat-CANARY-{Guid.NewGuid():N}";
        await using var gitLab = LoopbackResponder.Start(EchoThePresentedToken);
        var (viewer, team, repositoryId) = await SeedRepositoryAsync(token, gitLab.Port);

        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories/{repositoryId}/{page}", viewer, team);
        var body = await response.Content.ReadAsStringAsync();

        gitLab.Requests.ShouldContain(head => head.Contains(token), "fixture check: the provider was presented the token, so its 404 quoted it");
        ((int)response.StatusCode).ShouldBeInRange(400, 499, body);
        body.ShouldContain("Project Not Found", Case.Sensitive, "the provider's own words still reach the caller");
        body.ShouldNotContain(token, Case.Sensitive, $"/{page} reflected the presented token to a Viewer");
        body.ShouldNotContain("Original call", Case.Sensitive, "the SDK's internal request line is not the caller's to read");
    }

    private static string EchoThePresentedToken(string head)
    {
        var lines = head.Split("\r\n");
        string Header(string name) => lines.FirstOrDefault(line => line.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))?[(name.Length + 1)..].Trim() ?? "";
        var body = JsonSerializer.Serialize(new { message = $"404 Project Not Found (PRIVATE-TOKEN={Header("PRIVATE-TOKEN")}; Authorization={Header("Authorization")})" });

        return $"HTTP/1.1 404 Not Found\r\nContent-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\nConnection: close\r\n\r\n{body}";
    }

    /// <summary>An Owner connects GitLab and adds a PAT through the API, as an operator would; the repository row is seeded directly so no bind round-trip to the stub is needed.</summary>
    private async Task<(Guid Viewer, Guid Team, Guid RepositoryId)> SeedRepositoryAsync(string token, int port)
    {
        var owner = await _tenants.AccountAsync();
        var viewer = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (viewer, TeamRole.Viewer));

        var instanceId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/provider-instances", owner, team, new { provider = "GitLab", displayName = "echoing gitlab", baseUrl = $"http://127.0.0.1:{port}" }));
        var credentialId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/pat", owner, team, new { providerInstanceId = instanceId, displayName = "pat", token }));
        var repositoryId = Guid.NewGuid();

        await _tenants.DbAsync(async db =>
        {
            db.Repository.Add(new Repository { Id = repositoryId, TeamId = team, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = "42", NamespacePath = "acme", Name = "web", FullPath = "acme/web", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "http://127.0.0.1/acme/web", Status = RepositoryStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            return await db.SaveChangesAsync();
        });

        return (viewer, team, repositoryId);
    }

    private static async Task<Guid> CreatedIdAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }
}

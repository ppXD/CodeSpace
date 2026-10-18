using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Repositories;
using Shouldly;

namespace CodeSpace.E2ETests.Repositories;

/// <summary>
/// The Pulls and Issues tabs' lists end where the API says they do. A page past the last one a list allows used to read
/// the last page again, so a client that kept turning pages kept receiving the same rows under a page number that said
/// it was further on. It is now a 200 with an empty page, and the provider is not asked.
///
/// <para>Driven through the real HTTP surface (query binding, the access behavior, the handler, the service, the real
/// GitLab provider over NGitLab), against a loopback GitLab that holds one row per list.</para>
///
/// <para>Fidelity: HIGH. Only GitLab is a stub, and it is a real socket the production client speaks HTTP to.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ListPagingPastTheCeilingE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TenantHarness _tenants;

    public ListPagingPastTheCeilingE2ETests(TaskLaunchApiFactory factory) { _tenants = new TenantHarness(factory); }

    [Theory]
    [InlineData("pull-requests", ListPullRequestsQuery.MaxPage)]
    [InlineData("issues", ListIssuesQuery.MaxPage)]
    public async Task A_page_past_the_last_one_the_list_allows_is_an_empty_200_and_GitLab_is_not_asked(string list, int lastPage)
    {
        await using var gitLab = LoopbackResponder.Start(OneRowPerList);
        var (viewer, team, repositoryId) = await SeedRepositoryAsync(gitLab.Port);

        var last = await ListAsync(viewer, team, repositoryId, list, lastPage);

        last.GetArrayLength().ShouldBe(1, "fixture check: the last page the list allows still answers, so an empty page past it is the ceiling and not an empty GitLab");
        var asked = gitLab.Requests.Count;

        asked.ShouldBeGreaterThan(0, "fixture check: serving the last page reached GitLab");

        foreach (var page in new[] { lastPage + 1, 1_000_000, int.MaxValue })
            (await ListAsync(viewer, team, repositoryId, list, page)).GetArrayLength().ShouldBe(0, $"/{list}?page={page}");

        gitLab.Requests.Count.ShouldBe(asked, "a page past the last one is answered without a request to GitLab");
    }

    [Theory]
    [InlineData("pull-requests")]
    [InlineData("issues")]
    public async Task A_page_too_large_for_an_int_is_refused_and_not_answered_with_another_pages_rows(string list)
    {
        // Past what a page index holds there is no page to name: the query's Page is an int, so binding refuses it before any
        // handler could read it as a missing page, and GitLab is not asked either.
        await using var gitLab = LoopbackResponder.Start(OneRowPerList);
        var (viewer, team, repositoryId) = await SeedRepositoryAsync(gitLab.Port);

        // Connecting the provider may already have asked GitLab (the endpoint probe on add); only what the refused list request asks counts.
        var asked = gitLab.Requests.Count;

        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories/{repositoryId}/{list}?page=3000000000&perPage=100", viewer, team);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest, await TenantHarness.DescribeAsync(response));
        gitLab.Requests.Count.ShouldBe(asked, "a refused request reaches no provider");
    }

    private async Task<JsonElement> ListAsync(Guid viewer, Guid team, Guid repositoryId, string list, int page)
    {
        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories/{repositoryId}/{list}?page={page}&perPage=100", viewer, team);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, await TenantHarness.DescribeAsync(response));

        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    /// <summary>One merge request and one issue, whatever page is asked for — a stub that cannot tell the ceiling's page from any other, so only the API's own ceiling can make a page empty.</summary>
    private static string OneRowPerList(string head)
    {
        const string author = """{ "id": 1, "username": "dev", "name": "Dev" }""";
        var requestLine = head.Split("\r\n")[0];

        if (requestLine.Contains("/merge_requests")) return LoopbackResponder.Ok($$"""[{ "id": 7, "iid": 7, "project_id": 42, "title": "Seven", "state": "opened", "source_branch": "f7", "target_branch": "main", "author": {{author}}, "labels": [], "created_at": "2026-09-24T08:00:00.000Z", "updated_at": "2026-09-24T08:00:00.000Z", "web_url": "https://gitlab.test/acme/web/-/merge_requests/7" }]""");
        if (requestLine.Contains("/issues")) return LoopbackResponder.Ok($$"""[{ "id": 7, "iid": 7, "project_id": 42, "title": "Issue 7", "state": "opened", "author": {{author}}, "labels": [], "created_at": "2026-09-24T08:00:00.000Z", "updated_at": "2026-09-24T08:00:00.000Z", "web_url": "https://gitlab.test/acme/web/-/issues/7" }]""");
        if (requestLine.Contains("/labels")) return LoopbackResponder.Ok("[]");

        return "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
    }

    /// <summary>An Owner connects GitLab and adds a PAT through the API, as an operator would; the repository row is seeded directly so no bind round-trip to the stub is needed.</summary>
    private async Task<(Guid Viewer, Guid Team, Guid RepositoryId)> SeedRepositoryAsync(int port)
    {
        var owner = await _tenants.AccountAsync();
        var viewer = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (viewer, TeamRole.Viewer));

        var instanceId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/provider-instances", owner, team, new { provider = "GitLab", displayName = "paging gitlab", baseUrl = $"http://127.0.0.1:{port}" }));
        var credentialId = await CreatedIdAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/pat", owner, team, new { providerInstanceId = instanceId, displayName = "pat", token = $"glpat-FAKE-{Guid.NewGuid():N}" }));
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

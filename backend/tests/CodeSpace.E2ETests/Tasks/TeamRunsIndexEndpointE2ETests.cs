using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// E2E coverage for <c>GET /api/workflows/runs</c> (the team runs index) through the REAL ASP.NET pipeline — JWT auth,
/// the X-Team-Id team-scope behavior, route binding, the controller, the team-scoped query. A quick task is launched,
/// then the index is fetched over real HTTP and asserted to contain that run; a request with no team header is
/// rejected fail-closed (the team is never taken from the wire body).
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + real launch path. POSIX-only for the launch (the
/// fake CLI is a /bin/sh script — Rule 12.1); the fail-closed case needs no agent and runs everywhere.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
[Collection(FakeCliHttpE2ECollection.Name)]   // serial with the other fake-CLI Http E2E classes — they share the process-wide CodexHarness.CommandEnvVar
public sealed class TeamRunsIndexEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public TeamRunsIndexEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Index_lists_a_launched_run_for_its_team()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new FakeCodexCli();

        var (userId, teamId) = await SeedTeamMembershipAsync();

        var runId = await LaunchQuickTaskAsync(userId, teamId);

        var response = await SendAsync(HttpMethod.Get, "/api/workflows/runs", userId, teamId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var page = await response.Content.ReadFromJsonAsync<RunPage>();
        page.ShouldNotBeNull();
        page!.Items.ShouldContain(r => r.Id == runId, "the launched top-level run appears in its team's runs index");
    }

    [Fact]
    public async Task Index_for_another_team_does_not_leak_the_run()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeCodexCli();

        var (userId, teamId) = await SeedTeamMembershipAsync();
        var (otherUserId, otherTeamId) = await SeedTeamMembershipAsync();

        var runId = await LaunchQuickTaskAsync(userId, teamId);

        var response = await SendAsync(HttpMethod.Get, "/api/workflows/runs", otherUserId, otherTeamId);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var page = await response.Content.ReadFromJsonAsync<RunPage>();
        page!.Items.ShouldNotContain(r => r.Id == runId, "the index is team-scoped — another team never sees the run");
    }

    [Fact]
    public async Task Index_applies_a_query_string_filter()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeCodexCli();

        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await LaunchQuickTaskAsync(userId, teamId);

        // Unfiltered → the launched task run is present.
        var all = await (await SendAsync(HttpMethod.Get, "/api/workflows/runs", userId, teamId)).Content.ReadFromJsonAsync<RunPage>();
        all!.Items.ShouldContain(r => r.Id == runId);

        // ?workflowId= binds from the query string and filters: a task run has a null workflowId, so any workflowId
        // filter excludes it — a deterministic proof the generic filter param flows controller → query → service.
        var filtered = await SendAsync(HttpMethod.Get, $"/api/workflows/runs?workflowId={Guid.NewGuid()}", userId, teamId);
        filtered.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(filtered));
        var page = await filtered.Content.ReadFromJsonAsync<RunPage>();
        page!.Items.ShouldNotContain(r => r.Id == runId, "the workflowId filter binds from the query string and excludes the null-workflow task run");
    }

    [Fact]
    public async Task Index_paginates_over_http_via_the_cursor()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new FakeCodexCli();

        var (userId, teamId) = await SeedTeamMembershipAsync();

        var runA = await LaunchQuickTaskAsync(userId, teamId);
        var runB = await LaunchQuickTaskAsync(userId, teamId);

        // First page of 1 — the wire shape must carry a NextCursor since a second run exists.
        var first = await SendAsync(HttpMethod.Get, "/api/workflows/runs?limit=1", userId, teamId);
        first.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(first));
        var page1 = await first.Content.ReadFromJsonAsync<RunPage>();
        page1!.Items.Count.ShouldBe(1, "limit=1 binds from the query string and caps the page");
        page1.NextCursor.ShouldNotBeNull("two runs, page of 1 → there must be a next cursor");

        // Second page via the cursor (URL-encoded) — the two pages must be disjoint and together cover both runs.
        var second = await SendAsync(HttpMethod.Get, $"/api/workflows/runs?limit=1&cursor={Uri.EscapeDataString(page1.NextCursor!)}", userId, teamId);
        second.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(second));
        var page2 = await second.Content.ReadFromJsonAsync<RunPage>();
        page2!.Items.Count.ShouldBe(1);

        var seen = new[] { page1.Items[0].Id, page2.Items[0].Id };
        seen.ShouldBeUnique("keyset pages must not overlap");
        seen.ShouldBe(new[] { runA, runB }, ignoreOrder: true, "the two pages together cover both launched runs");
    }

    [Fact]
    public async Task Index_without_a_team_header_is_rejected_fail_closed()
    {
        var (userId, _) = await SeedTeamMembershipAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/workflows/runs");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the team is never taken from the wire — no X-Team-Id → 403");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<Guid> LaunchQuickTaskAsync(Guid userId, Guid teamId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/runs")
        {
            Content = JsonContent.Create(new
            {
                taskText = "Work on the auth refactor",
                effort = "quick",
                harness = "codex-cli",
                runnerKind = "local",
                autonomy = "Confined",
                surfaceKind = "chat",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        var response = await _factory.CreateClient().SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<RunRow>();
        return body!.RunId;   // the launch result names the field RunId (index rows name it Id)
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }

    private sealed record RunRow
    {
        public Guid Id { get; init; }
        public Guid RunId { get; init; }   // the launch response names it RunId; the index row names it Id
    }

    private sealed record RunPage
    {
        public IReadOnlyList<RunRow> Items { get; init; } = [];
        public string? NextCursor { get; init; }
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedTeamMembershipAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();

        db.User.Add(new User { Id = userId, Email = $"e2e-{suffix}@test.local", Name = "E2E", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"e2e-{suffix}", Name = "E2E", Kind = TeamKind.Workspace, OwnerUserId = userId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return (userId, teamId);
    }

    private static string MintToken(Guid userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TaskLaunchApiFactory.JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(claims: claims, notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response) =>
        $"expected 200 but got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}

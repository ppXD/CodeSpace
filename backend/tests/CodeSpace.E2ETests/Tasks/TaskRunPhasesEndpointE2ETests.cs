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
using CodeSpace.Messages.Tasks.Phases;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// E2E coverage for <c>GET /api/workflows/runs/{runId}/phases</c> through the REAL ASP.NET pipeline — JWT auth, the X-Team-Id
/// team-scope behavior, route binding, the controller, the projector + sources, the GlobalExceptionFilter. A quick
/// task is launched + run to terminal, then its phases are fetched over real HTTP and asserted; a foreign / absent
/// run returns 404 (team-scoped, fail-closed).
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + real engine + real executor + LocalProcessRunner +
/// the real phase projector reading the durable substrate. POSIX-only for the launch (the fake CLI is a /bin/sh
/// script — Rule 12.1); the 404 cases need no agent and run everywhere.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
[Collection(FakeCliHttpE2ECollection.Name)]   // serial with the other fake-CLI Http E2E classes — they share the process-wide CodexHarness.CommandEnvVar
public sealed class TaskRunPhasesEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public TaskRunPhasesEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Get_phases_returns_the_phase_tree_for_a_launched_run()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new FakeCodexCli();

        var (userId, teamId) = await SeedTeamMembershipAsync();

        var runId = await LaunchQuickTaskAsync(userId, teamId);

        await _factory.JobClient.DrainAsync();

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/phases", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<TaskRunPhasesResponse>();
        body.ShouldNotBeNull();
        body!.RunId.ShouldBe(runId);
        body.RunStatus.ShouldBe(nameof(WorkflowRunStatus.Success), "the launched quick task ran to Success");
        body.Phases.ShouldNotBeEmpty("the run produced structural node phases");
        body.Phases.ShouldContain(p => p.Status == PhaseStatus.Succeeded, "at least one phase reached the Succeeded render status");

        var agentPhase = body.Phases.Where(p => p.Kind == "agent").ToList()
            .ShouldHaveSingleItem("the quick run's agent.run node surfaces as one 'agent' phase over real HTTP");
        // The node source stamps the ref's Status from the REAL team-scoped AgentRun row (Succeeded), not the NodeStatus.
        agentPhase.Agents.ShouldHaveSingleItem().Status.ShouldBe(nameof(AgentRunStatus.Succeeded));
    }

    [Fact]
    public async Task Get_phases_for_an_absent_run_is_404()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{Guid.NewGuid()}/phases", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "an absent run is 404 — the projector returns null and the controller conflates");
    }

    [Fact]
    public async Task Get_phases_without_a_team_header_is_rejected_fail_closed()
    {
        var (userId, _) = await SeedTeamMembershipAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/runs/{Guid.NewGuid()}/phases");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the team is never taken from the route — no X-Team-Id → 403");
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

        var body = await response.Content.ReadFromJsonAsync<LaunchResponse>();
        return body!.RunId;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }

    private sealed record LaunchResponse
    {
        public Guid RunId { get; init; }
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

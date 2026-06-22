using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
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
/// E2E coverage for the "Needs decision" queue at its canonical <c>GET /api/workflows/decisions</c> route AND the
/// deprecated <c>/api/decisions</c> alias (asserted in the same Theory so the route move stays non-breaking) through
/// the REAL ASP.NET pipeline — JWT auth, the X-Team-Id team-scope behavior, route binding, the controller, the
/// queue service, the GlobalExceptionFilter.
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + real query handler. No agent is launched (a fresh
/// team has an empty queue), so this runs everywhere (no POSIX guard needed).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class DecisionsEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public DecisionsEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Theory]
    [InlineData("/api/workflows/decisions")]   // canonical run-resource root
    [InlineData("/api/decisions")]             // deprecated alias — kept for a non-breaking migration
    public async Task List_pending_decisions_resolves_and_is_empty_for_a_fresh_team(string route)
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        var response = await SendAsync(HttpMethod.Get, route, userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<JsonElement[]>();
        body.ShouldNotBeNull();
        body!.ShouldBeEmpty("a fresh team has no pending decisions; the canonical route and the deprecated alias resolve to the same handler");
    }

    [Fact]
    public async Task List_pending_decisions_without_a_team_header_is_rejected_fail_closed()
    {
        var (userId, _) = await SeedTeamMembershipAsync();

        var request = new HttpRequestMessage(HttpMethod.Get, "/api/workflows/decisions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "the team is never taken from the route — no X-Team-Id → 403");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
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
        $"decisions GET expected 200 but got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}

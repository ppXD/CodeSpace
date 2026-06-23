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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// S3 CONTINUE through the REAL ASP.NET pipeline (<c>POST /api/workflows/runs</c>): a first launch opens a session
/// (turn 1); a SECOND launch carrying that <c>sessionId</c> in the body binds its run to the SAME session as turn 2
/// — proving the continue field model-binds and flows HTTP → command → service end to end. Asserts the STAGED rows
/// (the turn ordinal is written at launch); the engine/agent chain is not drained, so this is cross-platform.
///
/// <para>Tier: 🟢 High-fidelity Http E2E — real app host (JWT auth, X-Team-Id team scope, model binding, mediator,
/// the transactional command) + real Postgres. Its own <see cref="TaskLaunchApiFactory"/> (IClassFixture) gives an
/// isolated DB + deferred-job client, so the undrained launch jobs never touch another class.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class WorkSessionContinueEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public WorkSessionContinueEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Posting_with_a_session_id_continues_the_thread_as_the_next_turn()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        // Turn 1: a plain launch opens the thread.
        var first = await LaunchAsync(userId, teamId, taskText: "Open the thread", continueSessionId: null);
        first.SessionId.ShouldNotBe(Guid.Empty, "the first launch opens a session");

        // Turn 2: re-POST the SAME sessionId in the body → continue the thread.
        var second = await LaunchAsync(userId, teamId, taskText: "Follow up on it", continueSessionId: first.SessionId);
        second.SessionId.ShouldBe(first.SessionId, "the continue stays in the same thread through HTTP");
        second.RunId.ShouldNotBe(first.RunId, "a continue starts a NEW run (the next turn), not the same one");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var secondRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == second.RunId);
        secondRun.SessionId.ShouldBe(first.SessionId, "the follow-up run is bound to the original session");
        secondRun.SessionTurnIndex.ShouldBe(2, "the follow-up is the session's second top-level turn end to end");

        (await db.WorkSession.AsNoTracking().CountAsync(s => s.TeamId == teamId))
            .ShouldBe(1, "continuing must NOT open a second session");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<LaunchResponse> LaunchAsync(Guid userId, Guid teamId, string taskText, Guid? continueSessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/runs")
        {
            Content = JsonContent.Create(new
            {
                taskText,
                sessionId = continueSessionId,
                effort = "quick",
                autonomy = "Confined",
                surfaceKind = "chat",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        var response = await _factory.CreateClient().SendAsync(request);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: $"launch failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<LaunchResponse>();
        body.ShouldNotBeNull();
        return body!;
    }

    private sealed record LaunchResponse
    {
        public Guid RunId { get; init; }
        public Guid SessionId { get; init; }
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
}

using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Sessions;
using CodeSpace.Messages.Dtos.Sessions.Room;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Sessions;

/// <summary>
/// The Sessions read API through the REAL ASP.NET pipeline (JWT auth, X-Team-Id team scope, model binding, mediator) +
/// real Postgres. Seeds the thread the whole-product way — a launch (turn 1) + a continue (turn 2) via
/// <c>POST /api/workflows/runs</c> — then reads it back over HTTP: the sessions list (<c>GET /api/sessions</c>), the
/// conversation (<c>GET /api/sessions/{id}</c>), and the run→session resolver (<c>GET /api/workflows/runs/{runId}/session</c>),
/// plus the 404 paths (missing + cross-team). Runs are staged (the deferred-job client doesn't drain them), so this is
/// cross-platform — it proves the HTTP surface + projection, not execution outcomes.
///
/// <para>Tier: 🟢 High-fidelity Http E2E — real app host + real Postgres, its own <see cref="TaskLaunchApiFactory"/>
/// (IClassFixture) for an isolated DB.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class SessionsEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    public SessionsEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task The_thread_reads_back_as_a_conversation_over_http()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        var turn1 = await LaunchAsync(userId, teamId, "Add login", continueSessionId: null);
        var turn2 = await LaunchAsync(userId, teamId, "Now add logout", continueSessionId: turn1.SessionId);

        // The list surfaces the thread with its turn count.
        var list = await GetAsync<SessionPage>(userId, teamId, "/api/sessions");
        var row = list.Items.ShouldHaveSingleItem("the team's one thread");
        row.Id.ShouldBe(turn1.SessionId);
        row.Title.ShouldBe("Add login", "the thread title is the opening turn's goal");
        row.TurnCount.ShouldBe(2);

        // The detail reads the conversation, oldest turn first, each carrying the user's message (the run goal).
        var detail = await GetAsync<SessionDetail>(userId, teamId, $"/api/sessions/{turn1.SessionId}");
        detail.Turns.Select(t => t.TurnIndex).ShouldBe(new[] { 1, 2 });
        detail.Turns[0].UserMessage.ShouldBe("Add login");
        detail.Turns[1].UserMessage.ShouldBe("Now add logout");
        detail.AnchorTurnIndex.ShouldBeNull("entering by session id sets no anchor");

        // The run→session resolver returns the same thread, anchored at the run's turn.
        var byRun = await GetAsync<SessionDetail>(userId, teamId, $"/api/workflows/runs/{turn2.RunId}/session");
        byRun.Id.ShouldBe(turn1.SessionId);
        byRun.AnchorTurnIndex.ShouldBe(2, "the run-anchored entry scrolls to that run's turn");
    }

    [Fact]
    public async Task A_missing_session_is_404()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        var response = await SendAsync(userId, teamId, $"/api/sessions/{Guid.NewGuid()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Another_teams_session_is_404_never_leaked()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var (otherUser, otherTeam) = await SeedTeamMembershipAsync();

        var foreign = await LaunchAsync(otherUser, otherTeam, "Their work", continueSessionId: null);

        // Ask for the foreign session with MY credentials + team header.
        var detail = await SendAsync(userId, teamId, $"/api/sessions/{foreign.SessionId}");
        detail.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a cross-team session is an indistinguishable not-found");

        var byRun = await SendAsync(userId, teamId, $"/api/workflows/runs/{foreign.RunId}/session");
        byRun.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a cross-team run resolves to no thread");
    }

    [Fact]
    public async Task The_room_reads_back_over_http_focused_on_the_runs_turn()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        var turn1 = await LaunchAsync(userId, teamId, "Add login", continueSessionId: null);
        var turn2 = await LaunchAsync(userId, teamId, "Now add logout", continueSessionId: turn1.SessionId);

        // Entering by a run resolves the thread + focuses that run's turn.
        var byRun = await GetAsync<RoomView>(userId, teamId, $"/api/sessions/by-run/{turn2.RunId}/room");
        byRun.SessionId.ShouldBe(turn1.SessionId);
        byRun.Title.ShouldBe("Add login", "the room title is the opening turn's goal");
        byRun.AnchorBlockId.ShouldBe("turn-2", "the run-anchored entry focuses that run's turn");

        byRun.Blocks.OfType<UserMessageBlock>().Select(b => b.Text).ShouldBe(new[] { "Add login", "Now add logout" });

        var turns = byRun.Blocks.OfType<AssistantTurnBlock>().OrderBy(t => t.TurnIndex).ToList();
        turns.Select(t => t.TurnIndex).ShouldBe(new[] { 1, 2 });
        turns[^1].Actions.ShouldContain(a => a.Kind == RoomActionKind.OpenTrace, "every turn exposes its capability-aware actions");

        // The by-session room focuses the latest turn when no focus is given.
        var bySession = await GetAsync<RoomView>(userId, teamId, $"/api/sessions/{turn1.SessionId}/room");
        bySession.AnchorBlockId.ShouldBe("turn-2", "the session room defaults to the latest turn");
    }

    [Fact]
    public async Task A_foreign_runs_room_is_404_never_leaked()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var (otherUser, otherTeam) = await SeedTeamMembershipAsync();

        var foreign = await LaunchAsync(otherUser, otherTeam, "Their work", continueSessionId: null);

        var byRun = await SendAsync(userId, teamId, $"/api/sessions/by-run/{foreign.RunId}/room");
        byRun.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a cross-team run's room is an indistinguishable not-found");

        var bySession = await SendAsync(userId, teamId, $"/api/sessions/{foreign.SessionId}/room");
        bySession.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(Guid userId, Guid teamId, string path) where T : class
    {
        var response = await SendAsync(userId, teamId, path);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: $"GET {path} failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        var body = await response.Content.ReadFromJsonAsync<T>(Json);
        body.ShouldNotBeNull();
        return body!;
    }

    private async Task<HttpResponseMessage> SendAsync(Guid userId, Guid teamId, string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        return await _factory.CreateClient().SendAsync(request);
    }

    private async Task<LaunchResponse> LaunchAsync(Guid userId, Guid teamId, string taskText, Guid? continueSessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/runs")
        {
            Content = JsonContent.Create(new { taskText, sessionId = continueSessionId, effort = "quick", autonomy = "Confined", surfaceKind = "chat" }),
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

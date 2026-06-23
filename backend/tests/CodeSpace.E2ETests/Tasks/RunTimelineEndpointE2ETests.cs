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
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Tasks.Timeline;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// E2E coverage for <c>GET /api/workflows/runs/{runId}/timeline</c> through the REAL ASP.NET pipeline — JWT auth, the
/// X-Team-Id team-scope, route binding, the controller, the projector + the run-record source reading the durable
/// <c>workflow_run_record</c> ledger. A run + its lifecycle records are seeded directly (no agent launch needed), then
/// the timeline is fetched over real HTTP and asserted; a foreign / absent run returns 404 (team-scoped, fail-closed).
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + the real timeline projector reading the durable
/// substrate. No fake CLI / launch, so it runs on every platform.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class RunTimelineEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public RunTimelineEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Get_timeline_returns_the_run_narrative()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/timeline", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<RunTimelineResponse>();
        body.ShouldNotBeNull();
        body!.RunId.ShouldBe(runId);
        body.RunStatus.ShouldBe(nameof(WorkflowRunStatus.Failure));
        body.Events.Select(e => e.Title).ShouldBe(new[] { "Run started", "code started", "code failed", "Run failed" },
            "the lifecycle ledger projects chronologically over real HTTP; the log line is dropped");
        body.Events.ShouldContain(e => e.Severity == TimelineSeverity.Error, "the failure surfaces as an Error-severity event");
    }

    [Fact]
    public async Task Get_timeline_merges_agent_events_with_the_lifecycle()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);
        await SeedAgentEventAsync(teamId, runId, "code", AgentEventKind.FileChanged, "edited auth/session.ts");

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/timeline", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<RunTimelineResponse>();
        body.ShouldNotBeNull();
        body!.Events.ShouldContain(e => e.SourceKey == "run-record" && e.Title == "Run started", "the lifecycle source still contributes");
        body.Events.ShouldContain(e => e.SourceKey == "agent-events" && e.Title == "edited auth/session.ts" && e.AgentRunId != null,
            "the agent's file edit is merged into the same timeline over real HTTP, tagged with its agent");
    }

    [Fact]
    public async Task Get_timeline_includes_the_supervisor_decision_ledger()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);
        await SeedSupervisorDecisionAsync(teamId, runId, SupervisorDecisionKinds.Spawn, StagedAgents(2));

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/timeline", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<RunTimelineResponse>();
        body.ShouldNotBeNull();
        body!.Events.ShouldContain(e => e.SourceKey == "supervisor" && e.Title == "Supervisor spawned 2 agents" && e.Severity == TimelineSeverity.Success,
            "the supervisor decision ledger merges into the same timeline over real HTTP, with severity round-tripped");
    }

    [Fact]
    public async Task Get_timeline_for_an_absent_run_is_404()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{Guid.NewGuid()}/timeline", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a run that isn't the team's is 404 — team-scoped, no existence leak");
    }

    private async Task<Guid> SeedRunWithRecordsAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var t = DateTimeOffset.UtcNow;

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = t, VerifiedAt = t, NormalizedAt = t,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = t,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        AddRecord(db, runId, WorkflowRunRecordTypes.RunStarted, null, "{}", t);
        AddRecord(db, runId, WorkflowRunRecordTypes.NodeStarted, "code", "{}", t.AddSeconds(1));
        AddRecord(db, runId, WorkflowRunRecordTypes.Log, "code", """{"level":"info","message":"noise"}""", t.AddSeconds(2));
        AddRecord(db, runId, WorkflowRunRecordTypes.NodeFailed, "code", """{"error":"boom"}""", t.AddSeconds(3));
        AddRecord(db, runId, WorkflowRunRecordTypes.RunFailed, null, """{"error":"boom"}""", t.AddSeconds(4));

        await db.SaveChangesAsync();
        return runId;
    }

    private static void AddRecord(CodeSpaceDbContext db, Guid runId, string type, string? nodeId, string payload, DateTimeOffset at) =>
        db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = type, NodeId = nodeId, OccurredAt = at, PayloadJson = payload });

    private async Task SeedAgentEventAsync(Guid teamId, Guid runId, string nodeId, AgentEventKind kind, string text)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var agentId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.AgentRun.Add(new AgentRun
        {
            Id = agentId, TeamId = teamId, WorkflowRunId = runId, NodeId = nodeId, IterationKey = nodeId,
            Harness = "codex-cli", Status = AgentRunStatus.Succeeded, TaskJson = "{}",
            CreatedDate = now, CreatedBy = Guid.Empty, LastModifiedDate = now, LastModifiedBy = Guid.Empty,
        });
        db.AgentRunEvent.Add(new AgentRunEvent { Id = Guid.NewGuid(), AgentRunId = agentId, Kind = kind, Text = text, OccurredAt = now });

        await db.SaveChangesAsync();
    }

    private async Task SeedSupervisorDecisionAsync(Guid teamId, Guid runId, string kind, string? outcome)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;

        db.SupervisorDecisionRecord.Add(new SupervisorDecisionRecord
        {
            Id = Guid.NewGuid(), TeamId = teamId, SupervisorRunId = runId,
            DecisionKind = kind, Status = SupervisorDecisionStatus.Succeeded, PayloadJson = "{}", OutcomeJson = outcome,
            IdempotencyKey = Guid.NewGuid().ToString("N"), InputHash = "test",
            CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedDate = now, LastModifiedBy = SystemUsers.SeederId,
        });

        await db.SaveChangesAsync();
    }

    private static string StagedAgents(int count) =>
        JsonSerializer.Serialize(new { agentRunIds = Enumerable.Range(0, count).Select(_ => Guid.NewGuid().ToString()).ToArray() });

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
        $"expected 200 but got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}

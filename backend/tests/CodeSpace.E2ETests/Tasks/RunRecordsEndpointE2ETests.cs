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
using CodeSpace.Messages.Tasks.Trace;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// E2E coverage for <c>GET /api/workflows/runs/{runId}/records</c> through the REAL ASP.NET pipeline — JWT auth, the
/// X-Team-Id team-scope, route binding, the controller, the <c>IRunRecordReader</c> dumping the durable
/// <c>workflow_run_record</c> ledger UNFILTERED. A run + its lifecycle records are seeded directly, then the raw audit
/// is fetched over real HTTP and asserted; a foreign / absent run returns 404 (team-scoped, fail-closed).
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + the real reader reading the durable substrate.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class RunRecordsEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public RunRecordsEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Get_records_returns_the_raw_unfiltered_ledger()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/records", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<RunRecordsResponse>();
        body.ShouldNotBeNull();
        body!.RunId.ShouldBe(runId);
        body.RunStatus.ShouldBe(nameof(WorkflowRunStatus.Failure));
        body.Records.Select(r => r.RecordType).ShouldBe(new[]
        {
            WorkflowRunRecordTypes.RunStarted, WorkflowRunRecordTypes.ScopeResolved, WorkflowRunRecordTypes.Log,
            WorkflowRunRecordTypes.NodeStarted, WorkflowRunRecordTypes.RunFailed,
        }, "the raw ledger projects over real HTTP in Sequence order — the scope + log records the narrative drops are present");

        body.Records.Single(r => r.RecordType == WorkflowRunRecordTypes.Log).PayloadJson
            .ShouldContain("noise", customMessage: "the raw payload is carried verbatim, not a derived narrative title");
    }

    [Fact]
    public async Task Get_records_for_an_absent_run_is_404()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();

        var response = await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{Guid.NewGuid()}/records", userId, teamId);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, "a run that isn't the team's is 404 — team-scoped, no existence leak");
    }

    [Fact]
    public async Task Get_records_stream_tails_the_ledger_as_server_sent_events_and_ends_at_the_terminal_record()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamId);   // ends with run.failed → the terminal record closes the stream itself

        using var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);   // safety net — a correct tail closes the stream at the terminal record
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/stream?after=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));
        response.Content.Headers.ContentType!.MediaType.ShouldBe("text/event-stream", "the tail is a Server-Sent Events stream");

        var body = await response.Content.ReadAsStringAsync();   // completes when the terminal record closes the stream

        body.ShouldContain("event: " + WorkflowRunRecordTypes.RunStarted, customMessage: "the lifecycle records stream as SSE events");
        body.ShouldContain("event: " + WorkflowRunRecordTypes.RunFailed, customMessage: "the terminal record is streamed");
        body.ShouldContain("id: ", customMessage: "each frame carries its Sequence as the SSE id — Last-Event-ID / ?after= resume");
        body.ShouldContain("\ndata: {", customMessage: "each frame carries a JSON data payload");
        body.TrimEnd().ShouldEndWith("}", customMessage: "the stream ends right after the terminal record's frame — it does not hang open");
    }

    [Fact]
    public async Task Get_records_stream_for_a_foreign_run_yields_an_empty_stream()
    {
        var (_, teamA) = await SeedTeamMembershipAsync();
        var runId = await SeedRunWithRecordsAsync(teamA);
        var (userB, teamB) = await SeedTeamMembershipAsync();

        using var client = _factory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(30);
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/workflows/runs/{runId}/records/stream?after=0");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userB));
        request.Headers.Add("X-Team-Id", teamB.ToString());

        var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        body.ShouldBeEmpty("a foreign run streams NO events — the run precheck is the tenancy boundary, no ledger leak");
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

        await db.SaveChangesAsync();   // persist the run + request first

        var records = new (string Type, string? NodeId, string Payload)[]
        {
            (WorkflowRunRecordTypes.RunStarted, null, "{}"),
            (WorkflowRunRecordTypes.ScopeResolved, null, """{"repos":1}"""),
            (WorkflowRunRecordTypes.Log, "code", """{"level":"info","message":"noise"}"""),
            (WorkflowRunRecordTypes.NodeStarted, "code", "{}"),
            (WorkflowRunRecordTypes.RunFailed, null, """{"error":"boom"}"""),
        };

        for (var i = 0; i < records.Length; i++)
        {
            var (type, nodeId, payload) = records[i];
            db.WorkflowRunRecord.Add(new WorkflowRunRecord { Id = Guid.NewGuid(), RunId = runId, RecordType = type, NodeId = nodeId, OccurredAt = t.AddSeconds(i), PayloadJson = payload });

            // Save per row so the BIGSERIAL Sequence increments in add-order (a batched save does not guarantee it).
            await db.SaveChangesAsync();
        }

        return runId;
    }

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

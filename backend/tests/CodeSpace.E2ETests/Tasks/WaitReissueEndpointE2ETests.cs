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
/// The operator wait-reissue verb through the REAL ASP.NET pipeline (<c>POST /api/workflows/runs/{runId}/waits/{waitId}/reissue</c>):
/// a run parked on a stranded Timer wait is force-resolved over HTTP → command → service → the resume CAS, un-stranding
/// the run and resolving the wait. Proves the route + <c>[FromBody]</c> (empty-body) binding, JWT + X-Team-Id scope, and
/// the <c>{ outcome, reissued }</c> shape end to end.
///
/// <para>Tier: 🟢 High-fidelity Http E2E — real app host (auth, team scope, model binding, mediator, the transactional
/// command) + real Postgres. The undrained re-dispatch job never runs a worker, so this is cross-platform.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class WaitReissueEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public WaitReissueEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Posting_reissue_for_a_stranded_timer_force_resolves_the_wait_and_un_strands_the_run()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var (runId, waitId) = await SeedSuspendedTimerRunAsync(teamId);

        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/workflows/runs/{runId}/waits/{waitId}/reissue")
        {
            Content = JsonContent.Create(new { }),   // no body — a Timer fires with the standard wake marker
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: $"reissue failed: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");

        var body = await response.Content.ReadFromJsonAsync<ReissueResponse>();
        body.ShouldNotBeNull();
        body!.Reissued.ShouldBeTrue("the stranded timer was force-resolved");
        body.Outcome.ShouldBe("Reissued");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        (await db.WorkflowRunWait.AsNoTracking().Where(w => w.Id == waitId).Select(w => w.Status).SingleAsync())
            .ShouldBe(WorkflowWaitStatuses.Resolved, "the timer wake was fired through the HTTP pipeline");
        (await db.WorkflowRun.AsNoTracking().Where(r => r.Id == runId).Select(r => r.Status).SingleAsync())
            .ShouldNotBe(WorkflowRunStatus.Suspended, "the run un-stranded (Suspended → Pending → the post-commit dispatch)");
        (await db.WorkflowRunRecord.AsNoTracking().AnyAsync(r => r.RunId == runId && r.RecordType == WorkflowRunRecordTypes.WaitReissued))
            .ShouldBeTrue("the override is audited on the ledger");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private sealed record ReissueResponse
    {
        public string Outcome { get; init; } = "";
        public bool Reissued { get; init; }
    }

    private async Task<(Guid RunId, Guid WaitId)> SeedSuspendedTimerRunAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var now = DateTimeOffset.UtcNow;
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var waitId = Guid.NewGuid();

        db.WorkflowRunRequest.Add(new WorkflowRunRequest
        {
            Id = requestId, TeamId = teamId, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot,
            ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}",
            Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now,
        });
        db.WorkflowRun.Add(new WorkflowRun
        {
            Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = teamId, RunRequestId = requestId,
            SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Suspended,
            ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now,
            CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId,
        });

        // Run BEFORE wait: EF doesn't model the wait → run relationship, so one SaveChanges could insert the wait first.
        await db.SaveChangesAsync();

        db.WorkflowRunWait.Add(new WorkflowRunWait
        {
            Id = waitId, RunId = runId, NodeId = "delay", IterationKey = string.Empty, WaitKind = WorkflowWaitKinds.Timer,
            Token = Guid.NewGuid().ToString("N"), WakeAt = now.AddMinutes(-1), Status = WorkflowWaitStatuses.Pending,
            PayloadJson = null, CreatedAt = now,
        });

        await db.SaveChangesAsync();
        return (runId, waitId);
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

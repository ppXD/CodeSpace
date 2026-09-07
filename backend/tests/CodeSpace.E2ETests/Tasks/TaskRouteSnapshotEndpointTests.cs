using System.Net;
using CodeSpace.Messages.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>Real HTTP/auth/Postgres contract tests. Jobs are never drained; no CLI or model-intelligence claim.</summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
[Collection(PostgresCollection.Name)]
public sealed class TaskRouteSnapshotEndpointTests : IClassFixture<TaskRouteSnapshotApiFactory>
{
    private readonly TaskRouteSnapshotApiFactory _factory;

    public TaskRouteSnapshotEndpointTests(TaskRouteSnapshotApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Preview_returns_a_durable_reference_without_opening_a_run_or_session()
    {
        var actor = await SeedAsync();
        using var response = await PostAsync(actor, "/api/workflows/runs/route-preview", Input());
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        body.TryGetProperty("routeSnapshotId", out var id).ShouldBeTrue("preview must return the server-owned decision reference");
        id.GetGuid().ShouldNotBe(Guid.Empty);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        body.GetProperty("expiresAt").GetDateTimeOffset().ShouldBeGreaterThan(await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync());
        (await db.WorkflowRun.CountAsync(r => r.TeamId == actor.TeamId)).ShouldBe(0);
        (await db.WorkSession.CountAsync(r => r.TeamId == actor.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task An_unknown_snapshot_reference_is_rejected_before_any_run_is_staged()
    {
        var actor = await SeedAsync();
        var input = Input();
        input["routeSnapshotId"] = Guid.NewGuid();
        using var response = await PostAsync(actor, "/api/workflows/runs", input);
        response.StatusCode.ShouldBe(HttpStatusCode.NotFound, await response.Content.ReadAsStringAsync());
        using var scope = _factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRun.CountAsync(r => r.TeamId == actor.TeamId)).ShouldBe(0);
    }

    [Fact]
    public async Task Auto_preview_launch_and_retry_use_one_router_decision_and_one_run()
    {
        var actor = await SeedAsync();
        var input = Input();
        input["effort"] = "auto";
        var before = _factory.Calls.Routes;
        var preview = await PreviewAsync(actor, input);
        _factory.Calls.Routes.ShouldBe(before + 1);
        input["routeSnapshotId"] = preview.GetProperty("routeSnapshotId").GetGuid();
        using var launch = await PostAsync(actor, "/api/workflows/runs", input);
        launch.StatusCode.ShouldBe(HttpStatusCode.OK, await launch.Content.ReadAsStringAsync());
        var result = await launch.Content.ReadFromJsonAsync<JsonElement>();
        JsonEqual(result.GetProperty("route"), preview.GetProperty("route"));
        using var retry = await PostAsync(actor, "/api/workflows/runs", input);
        retry.StatusCode.ShouldBe(HttpStatusCode.OK, await retry.Content.ReadAsStringAsync());
        JsonEqual(await retry.Content.ReadFromJsonAsync<JsonElement>(), result);
        _factory.Calls.Routes.ShouldBe(before + 1, "launch and retry must not re-enter the classifier/router");
        await AssertOneRunAsync(actor.TeamId);
    }

    [Theory]
    [InlineData("taskText", "A different task")]
    [InlineData("effort", "deep")]
    [InlineData("recipe", "map-fanout")]
    [InlineData("autonomy", "Trusted")]
    [InlineData("harness", "claude-code")]
    [InlineData("timeoutSeconds", 60)]
    [InlineData("pushBranch", true)]
    [InlineData("requirePlanConfirmation", true)]
    [InlineData("outputReviewMode", "Gate")]
    [InlineData("tier", "Delivery")]
    public async Task Changed_goal_or_control_rejects_the_reference_without_rerouting(string field, object value)
    {
        var actor = await SeedAsync();
        var input = Input();
        var preview = await PreviewAsync(actor, input);
        var before = _factory.Calls.Routes;
        input["routeSnapshotId"] = preview.GetProperty("routeSnapshotId").GetGuid();
        input[field] = value;
        using var response = await PostAsync(actor, "/api/workflows/runs", input);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        _factory.Calls.Routes.ShouldBe(before);
        await AssertNoRunAsync(actor.TeamId);
    }

    [Fact]
    public async Task Foreign_actor_and_team_cannot_consume_or_read_a_completed_reference()
    {
        var actor = await SeedAsync();
        var foreign = await SeedAsync();
        await AddMembershipAsync(foreign.UserId, actor.TeamId);
        await AddMembershipAsync(actor.UserId, foreign.TeamId);
        var input = Input();
        input["routeSnapshotId"] = (await PreviewAsync(actor, input)).GetProperty("routeSnapshotId").GetGuid();
        foreach (var target in new[] { (foreign.UserId, actor.TeamId), (actor.UserId, foreign.TeamId) })
        {
            using var denied = await PostAsync(target, "/api/workflows/runs", input);
            denied.StatusCode.ShouldBe(HttpStatusCode.NotFound, await denied.Content.ReadAsStringAsync());
        }
        using var launched = await PostAsync(actor, "/api/workflows/runs", input);
        launched.StatusCode.ShouldBe(HttpStatusCode.OK, await launched.Content.ReadAsStringAsync());
        using var deniedReplay = await PostAsync((foreign.UserId, actor.TeamId), "/api/workflows/runs", input);
        deniedReplay.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().TeamMembership.Where(m => m.TeamId == actor.TeamId && m.UserId == actor.UserId).ExecuteDeleteAsync();
        using var revokedReplay = await PostAsync(actor, "/api/workflows/runs", input);
        revokedReplay.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "a consumed route reference cannot bypass fresh membership checks");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Expiry_or_policy_rotation_rejects_an_unconsumed_reference(bool expired)
    {
        var actor = await SeedAsync();
        var input = Input();
        var id = (await PreviewAsync(actor, input)).GetProperty("routeSnapshotId").GetGuid();
        input["routeSnapshotId"] = id;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var databaseNow = await db.Database.SqlQueryRaw<DateTimeOffset>("SELECT clock_timestamp() AS \"Value\"").SingleAsync();
        if (expired) await db.TaskRouteSnapshot.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, databaseNow.AddMinutes(-1)));
        else await db.TaskRouteSnapshot.Where(s => s.Id == id).ExecuteUpdateAsync(s => s.SetProperty(r => r.PolicyFingerprint, "prior-policy"));
        using var response = await PostAsync(actor, "/api/workflows/runs", input);
        response.StatusCode.ShouldBe(HttpStatusCode.Conflict, await response.Content.ReadAsStringAsync());
        await AssertNoRunAsync(actor.TeamId);
        (await db.TaskRouteSnapshot.AsNoTracking().SingleAsync(s => s.Id == id)).ConsumedRunId.ShouldBeNull();
    }

    [Fact]
    public async Task Concurrent_requests_across_hosts_commit_one_run_and_return_the_same_result()
    {
        var actor = await SeedAsync();
        var input = Input();
        input["routeSnapshotId"] = (await PreviewAsync(actor, input)).GetProperty("routeSnapshotId").GetGuid();
        using var otherHost = _factory.WithWebHostBuilder(_ => { });
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => PostAsync(actor, "/api/workflows/runs", input, i % 2 == 0 ? _factory : otherHost)));
        try
        {
            var results = new List<JsonElement>();
            foreach (var response in responses)
            {
                response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
                results.Add(await response.Content.ReadFromJsonAsync<JsonElement>());
            }
            foreach (var result in results) JsonEqual(result, results[0]);
            await AssertOneRunAsync(actor.TeamId);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task Failure_after_staging_rolls_back_consumption_and_the_same_reference_can_retry()
    {
        var actor = await SeedAsync();
        var input = Input();
        var id = (await PreviewAsync(actor, input)).GetProperty("routeSnapshotId").GetGuid();
        input["routeSnapshotId"] = id;
        _factory.Calls.FailNextStage = true;
        using var failed = await PostAsync(actor, "/api/workflows/runs", input);
        failed.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        await AssertNoRunAsync(actor.TeamId);
        using (var scope = _factory.Services.CreateScope())
            (await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().TaskRouteSnapshot.AsNoTracking().SingleAsync(s => s.Id == id)).ConsumedRunId.ShouldBeNull();
        using var retried = await PostAsync(actor, "/api/workflows/runs", input);
        retried.StatusCode.ShouldBe(HttpStatusCode.OK, await retried.Content.ReadAsStringAsync());
        await AssertOneRunAsync(actor.TeamId);
    }

    private async Task<JsonElement> PreviewAsync((Guid UserId, Guid TeamId) actor, Dictionary<string, object?> input)
    {
        using var response = await PostAsync(actor, "/api/workflows/runs/route-preview", input);
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static void JsonEqual(JsonElement actual, JsonElement expected) => CodeSpace.Messages.Agents.ToolCallKey.Canonicalize(actual).ShouldBe(CodeSpace.Messages.Agents.ToolCallKey.Canonicalize(expected));

    private async Task AssertNoRunAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        (await db.WorkflowRun.CountAsync(r => r.TeamId == teamId)).ShouldBe(0);
        (await db.WorkSession.CountAsync(r => r.TeamId == teamId)).ShouldBe(0);
    }

    private async Task AssertOneRunAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        (await db.WorkflowRun.CountAsync(r => r.TeamId == teamId)).ShouldBe(1);
        (await db.WorkSession.CountAsync(r => r.TeamId == teamId)).ShouldBe(1);
        var snapshot = await db.TaskRouteSnapshot.AsNoTracking().SingleAsync(s => s.TeamId == teamId);
        snapshot.ConsumedRunId.ShouldNotBeNull();
        snapshot.ResultJson.ShouldNotBeNullOrWhiteSpace();
        (await db.WorkflowRun.SingleAsync(r => r.TeamId == teamId)).Id.ShouldBe(snapshot.ConsumedRunId.Value);
    }

    private async Task AddMembershipAsync(Guid userId, Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
    }

    private static Dictionary<string, object?> Input() => new() { ["taskText"] = "Explain the launch snapshot contract", ["effort"] = "quick", ["autonomy"] = "Confined", ["surfaceKind"] = "chat" };

    private async Task<HttpResponseMessage> PostAsync((Guid UserId, Guid TeamId) actor, string path, object input, WebApplicationFactory<CodeSpace.Api.Program>? application = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(input) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(actor.UserId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", actor.TeamId.ToString());
        return await (application ?? _factory).CreateClient().SendAsync(request);
    }

    private async Task<(Guid UserId, Guid TeamId)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var userId = Guid.NewGuid();
        var teamId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"route-{suffix}@test.local", Name = "Route", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"route-{suffix}", Name = "Route", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        await db.SaveChangesAsync();
        return (userId, teamId);
    }
}

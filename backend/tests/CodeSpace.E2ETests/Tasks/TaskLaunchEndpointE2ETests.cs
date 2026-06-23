using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Agents;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Shouldly;

namespace CodeSpace.E2ETests.Tasks;

/// <summary>
/// E2E coverage for <c>POST /api/workflows/runs</c> through the REAL ASP.NET pipeline — JWT auth, the X-Team-Id
/// team-scope behavior (TeamMembershipAuthorizationBehavior), model binding of <c>LaunchTaskCommand</c>, the
/// controller, the mediator, the GlobalExceptionFilter. The launch's post-commit dispatch → engine run →
/// agent.code → real executor + LocalProcessRunner + fake CLI → resume → terminal chain is then DRAINED and the
/// run asserted to reach Success — proving the generic launch surface works end to end behind real HTTP.
///
/// <para>Tier: 🟢 High-fidelity — real app host + real Postgres + real engine + real executor + LocalProcessRunner
/// spawning a real OS process; only the CLI's intelligence is faked at the binary (<see cref="FakeCodexCli"/>) and
/// the Hangfire transport is the in-test deferred queue. POSIX-only (the fake CLI is a /bin/sh script — Rule 12.1).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
[Collection(FakeCliHttpE2ECollection.Name)]   // serial with the other fake-CLI Http E2E classes — they share the process-wide CodexHarness.CommandEnvVar
public sealed class TaskLaunchEndpointE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;

    public TaskLaunchEndpointE2ETests(TaskLaunchApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task Post_tasks_launches_a_chat_task_that_runs_a_real_agent_to_success()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new FakeCodexCli();

        var (userId, teamId) = await SeedTeamMembershipAsync();

        // POST through the REAL pipeline: a signed bearer token + the X-Team-Id header drive auth + team scope.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/runs")
        {
            Content = JsonContent.Create(new
            {
                taskText = "Work on the auth refactor",
                effort = "quick",   // quick → single-agent (the shape this single-agent E2E asserts); standard → map-fanout, deep → supervisor
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
        body.ShouldNotBeNull();
        body!.RunId.ShouldNotBe(Guid.Empty, "the launch endpoint returns the started run id");
        body.SessionId.ShouldNotBe(Guid.Empty, "the launch endpoint returns the opened work-session id");
        body.ProjectionKind.ShouldBe("single-agent");
        body.SurfaceKind.ShouldBe("chat");

        // Drain the deferred chain: the launch's post-commit dispatch enqueued the engine run; that run's
        // agent.code suspend enqueued the real executor; draining runs the whole chain to terminal.
        await _factory.JobClient.DrainAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == body.RunId);
        run.Status.ShouldBe(WorkflowRunStatus.Success,
            customMessage: $"the launched chat task must run to Success through the real HTTP → launch → engine → executor → fake CLI chain; inspect WorkflowRunNode + AgentRun.Error for run {body.RunId}");

        // It is a SNAPSHOT run — no Workflow row, end to end through the real endpoint.
        run.WorkflowId.ShouldBeNull("a launched task run is a snapshot run — not a child of any workflow");
        run.WorkflowVersion.ShouldBeNull();
        (await db.Workflow.AsNoTracking().CountAsync(w => w.TeamId == teamId)).ShouldBe(0, "no workflow row is created for a launched snapshot run");

        // The launch opened a WorkSession (Kind=Task) and bound the run to it as turn 1 — and the binding SURVIVED
        // the whole HTTP → engine → agent → terminal walk (the executor's status/output writes never clobbered it).
        var session = await db.WorkSession.AsNoTracking().SingleAsync(s => s.Id == body.SessionId);
        session.TeamId.ShouldBe(teamId, "the opened session is scoped to the launching team");
        session.Kind.ShouldBe(WorkSessionKind.Task, "a launched task opens a Task-kind thread");
        run.SessionId.ShouldBe(body.SessionId, "the run stays bound to its session through the full engine walk");
        run.SessionTurnIndex.ShouldBe(1, "the launch run is the session's first turn");

        var agentRun = await db.AgentRun.AsNoTracking().SingleAsync(r => r.WorkflowRunId == body.RunId);
        agentRun.Status.ShouldBe(AgentRunStatus.Succeeded, "the launched agent.code executed to Succeeded via the real executor + runner + fake CLI");

        // The folded summary must be the fake-CLI transform of the POSTed task text — proving the operator's exact
        // goal reached the real CLI end-to-end through HTTP (not just "some agent exited 0"). This assertion is also
        // the drift signal for FakeCodexCli's inline mirror (Rule 12.5): a stale event shape fails the fold here.
        var folded = JsonSerializer.Deserialize<AgentRunResult>(agentRun.ResultJson!, AgentJson.Options)!;
        folded.Summary.ShouldBe(FakeCodexCli.ExpectedSummaryFor("Work on the auth refactor"),
            customMessage: "the folded summary must match the launched task text through the real CLI; a mismatch means the goal never propagated end-to-end or the fake-CLI event shape drifted");
    }

    [Fact]
    public async Task Post_continue_carries_the_first_turns_real_summary_into_the_next_agent_prompt()
    {
        if (OperatingSystem.IsWindows()) return;   // the fake CLI is a /bin/sh script the runner spawns

        using var cli = new FakeCodexCli();

        var (userId, teamId) = await SeedTeamMembershipAsync();

        // Turn 1: launch + run a real agent to a real summary through the whole HTTP → engine → executor → fake CLI chain.
        const string firstGoal = "Work on the auth refactor";
        var first = await PostLaunchAsync(userId, teamId, firstGoal, continueSessionId: null);
        await _factory.JobClient.DrainAsync();

        var realSummary = FakeCodexCli.ExpectedSummaryFor(firstGoal);

        // Turn 2: continue the SAME thread through HTTP — its agent prompt must carry turn 1's REAL summary forward.
        var second = await PostLaunchAsync(userId, teamId, "Now also rotate the tokens", continueSessionId: first.SessionId);
        second.SessionId.ShouldBe(first.SessionId, "the follow-up stays in the same thread through HTTP");
        await _factory.JobClient.DrainAsync();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var secondRun = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == second.RunId);
        secondRun.SessionTurnIndex.ShouldBe(2, "the continue is the thread's second turn");

        var secondAgentGoal = ReadAgentGoal(secondRun.DefinitionSnapshotJson!);
        secondAgentGoal.ShouldContain(realSummary,
            customMessage: "turn 2's agent prompt must carry turn 1's REAL produced summary forward — whole-product multi-turn context propagation through HTTP");
        secondAgentGoal.ShouldContain("Now also rotate the tokens", customMessage: "and still address the new follow-up task");
    }

    [Fact]
    public async Task Post_tasks_with_related_repositories_binds_the_multi_repo_workspace_through_real_http()
    {
        var (userId, teamId) = await SeedTeamMembershipAsync();
        var primary = await SeedRepositoryAsync(teamId, "api");
        var web = await SeedRepositoryAsync(teamId, "web");

        // POST a MULTI-REPO launch through the REAL pipeline — the new relatedRepositories array must bind through
        // ASP.NET model binding → LaunchTaskCommand → request → profile → projection. Frozen-def assertion only (a
        // real multi-repo clone of seed-only repos would fail; the run path is covered by the single-agent E2E above).
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/runs")
        {
            Content = JsonContent.Create(new
            {
                taskText = "Coordinated change across api + web",
                repositoryId = primary,
                relatedRepositories = new[] { new { repositoryId = web, alias = "web", access = "write" } },
                effort = "quick",
                surfaceKind = "chat",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: await DescribeFailureAsync(response));

        var body = await response.Content.ReadFromJsonAsync<LaunchResponse>();
        body.ShouldNotBeNull();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(r => r.Id == body!.RunId);

        // The related repo bound through HTTP and round-tripped into the frozen agent.code inputs…
        ReadAgentRelatedRepositoryIds(run.DefinitionSnapshotJson!).ShouldBe(new[] { web.ToString() },
            customMessage: "the relatedRepositories array must bind through real ASP.NET model binding into the projected agent.code inputs");

        // …and the run scope is the primary + the related repo (what a multi-repo session-branch resolver scans).
        run.ScopeRepositoryIds.ShouldBe(new[] { primary, web }, ignoreOrder: true,
            customMessage: "the launch scope folds the primary + related repos through the full HTTP pipeline");
    }

    [Fact]
    public async Task Post_tasks_without_a_team_header_is_rejected_fail_closed()
    {
        var (userId, _) = await SeedTeamMembershipAsync();

        // A signed token but NO X-Team-Id header → the team-scope behavior throws (team comes from the header,
        // never the body) → the GlobalExceptionFilter maps it to 403. Tenancy fail-closed at the HTTP boundary.
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/runs")
        {
            Content = JsonContent.Create(new { taskText = "no team header", surfaceKind = "chat" }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", MintToken(userId));

        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden,
            customMessage: "a launch with no X-Team-Id must be rejected — the team is never taken from the body");
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────

    /// <summary>POST a launch (optionally continuing a session) through the real HTTP pipeline + return the parsed result.</summary>
    private async Task<LaunchResponse> PostLaunchAsync(Guid userId, Guid teamId, string taskText, Guid? continueSessionId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows/runs")
        {
            Content = JsonContent.Create(new
            {
                taskText,
                sessionId = continueSessionId,
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
        body.ShouldNotBeNull();
        return body!;
    }

    /// <summary>Reads the projected agent.code node's <c>goal</c> (the agent prompt) out of the frozen definition snapshot.</summary>
    private static string ReadAgentGoal(string definitionSnapshotJson)
    {
        var root = JsonDocument.Parse(definitionSnapshotJson).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");
        return agent.GetProperty("config").GetProperty("goal").GetString()!;
    }

    /// <summary>Reads the projected agent.code node's <c>relatedRepositories</c> repo ids out of the frozen snapshot. Empty when the key is absent.</summary>
    private static IReadOnlyList<string> ReadAgentRelatedRepositoryIds(string definitionSnapshotJson)
    {
        var root = JsonDocument.Parse(definitionSnapshotJson).RootElement;
        var agent = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "agent");

        if (!agent.GetProperty("inputs").TryGetProperty("relatedRepositories", out var arr) || arr.ValueKind != JsonValueKind.Array) return [];

        return arr.EnumerateArray().Select(e => e.GetProperty("repositoryId").GetString()!).ToList();
    }

    /// <summary>Seeds a provider instance + an active repository in the team — enough to satisfy the launch's team-scope check.</summary>
    private async Task<Guid> SeedRepositoryAsync(Guid teamId, string name)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instanceId = Guid.NewGuid();
        var repoId = Guid.NewGuid();

        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitHub, DisplayName = "GH", BaseUrl = $"https://gh-{suffix}.local", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repoId, TeamId = teamId, ProviderInstanceId = instanceId, ExternalId = $"ext-{suffix}", NamespacePath = "acme", Name = name, FullPath = $"acme/{name}-{suffix}", WebUrl = $"https://gh.local/acme/{name}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return repoId;
    }

    private sealed record LaunchResponse
    {
        public Guid RunId { get; init; }
        public Guid SessionId { get; init; }
        public string ProjectionKind { get; init; } = "";
        public string SurfaceKind { get; init; } = "";
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

    /// <summary>Mints an HS256 bearer token with the user's NameIdentifier claim — signed with the host's symmetric key so the real JWT auth handler accepts it (issuer/audience validation is off).</summary>
    private static string MintToken(Guid userId)
    {
        var claims = new[] { new Claim(ClaimTypes.NameIdentifier, userId.ToString()) };
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(TaskLaunchApiFactory.JwtKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var jwt = new JwtSecurityToken(claims: claims, notBefore: DateTime.UtcNow, expires: DateTime.UtcNow.AddHours(1), signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task<string> DescribeFailureAsync(HttpResponseMessage response) =>
        $"POST /api/workflows/runs expected 200 but got {(int)response.StatusCode}; body: {await response.Content.ReadAsStringAsync()}";
}

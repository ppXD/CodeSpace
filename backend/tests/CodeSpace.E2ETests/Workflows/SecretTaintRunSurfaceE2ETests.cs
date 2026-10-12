using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Autofac;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Artifacts;
using CodeSpace.Core.Services.Workflows.Runtime;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.IntegrationTests.Workflows.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Workflows;

/// <summary>
/// The operator's whole path through HTTP: an Owner stores a team secret and a workflow secret, authors a workflow whose
/// <c>agent.run</c> goal and <c>flow.subworkflow</c> inputs reference them, and runs it; the run executes end to end
/// (engine → real executor → agent CLI → resume → child run → resume → terminal). Then a VIEWER of the same team reads
/// every run surface the SPA reads — run detail, records, timeline, pending wait, the agent run and its events, the
/// child's detail — and an all-table + artifact sweep checks what is at rest. Neither may hold either secret, while the
/// agent was launched with both and the child executed with both.
///
/// <para>Tier: 🟢 High-fidelity — the real ASP.NET pipeline (JWT, X-Team-Id, membership/permission behaviors,
/// controllers), real Postgres, real engine, real <c>AgentRunExecutor</c> + <c>LocalProcessRunner</c> spawning a real
/// process; only the CLI's intelligence is faked at the binary (<see cref="GoalCapturingFakeCli"/>) and the Hangfire
/// transport is the in-test deferred queue. POSIX-only (Rule 12.1).</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
[Collection(PostgresCollection.Name)]   // serial with every fake-arming class in this assembly — they share the process-wide CLI command env vars
public sealed class SecretTaintRunSurfaceE2ETests : IClassFixture<SecretTaintRunSurfaceE2ETests.ReviewedApiFactory>
{
    private readonly ReviewedApiFactory _factory;

    public SecretTaintRunSurfaceE2ETests(ReviewedApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_viewer_reads_no_secret_on_any_run_surface_and_none_lands_at_rest_while_the_run_uses_both()
    {
        if (OperatingSystem.IsWindows()) return;

        using var cli = new GoalCapturingFakeCli();
        var team = await SeedTeamAsync();
        var teamSecret = $"CANARY-e2e-team-{Guid.NewGuid():N}";
        var workflowSecret = $"CANARY-e2e-wf-{Guid.NewGuid():N}";

        await SendOkAsync(HttpMethod.Put, "/api/team-variables/DEPLOY_TOKEN", team.OwnerId, team.TeamId, new { name = "DEPLOY_TOKEN", valueType = "Secret", value = teamSecret });
        var childId = await CreateWorkflowAsync(team, ChildDefinition());
        var parentId = await CreateWorkflowAsync(team, ParentDefinition(childId));
        await SendOkAsync(HttpMethod.Put, $"/api/workflows/{parentId}/variables/WF_TOKEN", team.OwnerId, team.TeamId, new { name = "WF_TOKEN", valueType = "Secret", value = workflowSecret });

        var runId = (await SendOkAsync(HttpMethod.Post, $"/api/workflows/{parentId}/run", team.OwnerId, team.TeamId, new { })).GetProperty("runId").GetGuid();
        await _factory.JobClient.DrainAsync();

        var (agentRunId, childRunId) = await AssertRanToSuccessAsync(runId);
        cli.CapturedGoals.ShouldContain(teamSecret, customMessage: "the agent must be launched with the EXACT team secret its goal referenced");
        cli.CapturedGoals.ShouldContain(workflowSecret, customMessage: "… and the exact workflow secret");
        (await ChildStartOutputsAsync(childRunId))["wfToken"].GetString().ShouldBe(workflowSecret, "the child must execute with the parent's exact workflow secret");

        var viewerReads = await ViewerReadsAsync(team, runId, childRunId, agentRunId);

        foreach (var secret in new[] { teamSecret, workflowSecret })
        {
            viewerReads.ShouldNotContain(secret, customMessage: "a Viewer read a resolved secret off a run surface");
            (await SweepAsync(team.TeamId, secret)).ShouldBeEmpty("a resolved secret froze into a table or an artifact");
        }
    }

    [Fact]
    public async Task A_viewer_reads_no_secret_in_the_reviewers_verdict_or_the_acceptance_evidence_it_can_download()
    {
        // The goal and the acceptance argv both carry the team secret. The argv runs with it (the check passes only on
        // the exact value), the output reviewer is shown it and quotes it, and both the verdict's words and the grade's
        // evidence artifact reach surfaces a Viewer reads or downloads.
        if (OperatingSystem.IsWindows()) return;

        using var cli = new GoalCapturingFakeCli();
        var team = await SeedTeamAsync();
        var teamSecret = $"CANARY-e2e-review-{Guid.NewGuid():N}";
        var reviewerModelId = await SeedReviewerModelAsync(team.TeamId);

        await SendOkAsync(HttpMethod.Put, "/api/team-variables/DEPLOY_TOKEN", team.OwnerId, team.TeamId, new { name = "DEPLOY_TOKEN", valueType = "Secret", value = teamSecret });
        var workflowId = await CreateWorkflowAsync(team, ReviewedAgentDefinition(reviewerModelId));

        var runId = (await SendOkAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/run", team.OwnerId, team.TeamId, new { })).GetProperty("runId").GetGuid();
        await _factory.JobClient.DrainAsync();

        var (agentRunId, evidenceId) = await AssertReviewedAndGradedAsync(runId);
        cli.CapturedGoals.ShouldContain(teamSecret, customMessage: "the agent must be launched with the EXACT team secret its goal referenced");
        _factory.Services.GetRequiredService<CriticReviewScript>().Quoted.ShouldContain(quote => quote.Contains(teamSecret), "the reviewer was shown the EXACT goal and quoted it");

        var viewerReads = await ViewerReadsAsync(team, runId, null, agentRunId);
        var evidence = await SendAsync(HttpMethod.Get, $"/api/artifacts/{evidenceId}", team.ViewerId, team.TeamId);
        evidence.StatusCode.ShouldBe(HttpStatusCode.OK, "a Viewer downloads the acceptance evidence");
        var evidenceText = await evidence.Content.ReadAsStringAsync();

        evidenceText.ShouldContain($"graded with {PersistenceSecretRedactor.Marker}", customMessage: "the evidence still shows what the check printed — masked, not dropped");
        viewerReads.ShouldContain("The task was: ", customMessage: "the reviewer's verdict reaches the Viewer — masked, not dropped");
        evidenceText.ShouldNotContain(teamSecret, customMessage: "the acceptance evidence a Viewer downloads held the resolved secret");
        viewerReads.ShouldNotContain(teamSecret, customMessage: "a Viewer read a resolved secret off a run surface");
        (await SweepAsync(team.TeamId, teamSecret)).ShouldBeEmpty("a resolved secret froze into a table or an artifact");
    }

    // ── Definitions ──────────────────────────────────────────────────────────────────────────────────────────

    private static WorkflowDefinition ReviewedAgentDefinition(Guid reviewerModelId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = Json("{}"), Inputs = Json("{}") },
            new() { Id = "agent", TypeKey = "agent.run", Inputs = Json("{}"),
                    Config = Json($$$"""{ "goal": "Deploy with bearer {{team.DEPLOY_TOKEN}} {{{DeterministicCriticLlmClient.QuoteGoalMarker}}}", "harness": "codex-cli", "runnerKind": "local", "timeoutSeconds": 300, "outputReviewMode": 1, "reviewerModelId": "{{{reviewerModelId}}}", "acceptance": { "kind": "TestsPass", "command": ["sh", "-c", "test '{{team.DEPLOY_TOKEN}}' != '[REDACTED]' && echo graded with {{team.DEPLOY_TOKEN}}"] } }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = Json("{}"), Inputs = Json("""{ "status": "ok" }""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "agent" }, new() { From = "agent", To = "end" } },
    };

    private static WorkflowDefinition ParentDefinition(Guid childWorkflowId) => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = Json("{}"), Inputs = Json("{}") },
            new() { Id = "agent", TypeKey = "agent.run", Inputs = Json("{}"),
                    Config = Json("""{ "goal": "Deploy with bearer {{team.DEPLOY_TOKEN}} and sign with {{wf.WF_TOKEN}}", "harness": "codex-cli", "runnerKind": "local", "timeoutSeconds": 300 }""") },
            new() { Id = "sub", TypeKey = "flow.subworkflow", Config = Json($$"""{ "workflowId": "{{childWorkflowId}}" }"""),
                    Inputs = Json("""{ "inputs": { "token": "{{team.DEPLOY_TOKEN}}", "wfToken": "{{wf.WF_TOKEN}}" } }""") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = Json("{}"), Inputs = Json("""{ "status": "ok" }""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "agent" }, new() { From = "agent", To = "sub" }, new() { From = "sub", To = "end" } },
    };

    private static WorkflowDefinition ChildDefinition() => new()
    {
        SchemaVersion = 1,
        Nodes = new List<NodeDefinition>
        {
            new() { Id = "start", TypeKey = "trigger.manual", Config = Json("{}"), Inputs = Json("{}") },
            new() { Id = "end", TypeKey = "builtin.terminal", Config = Json("{}"), Inputs = Json("""{ "status": "ok" }""") },
        },
        Edges = new List<EdgeDefinition> { new() { From = "start", To = "end" } },
    };

    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement.Clone();

    // ── HTTP ─────────────────────────────────────────────────────────────────────────────────────────────────

    private sealed record Team(Guid TeamId, Guid OwnerId, Guid ViewerId);

    private async Task<Guid> CreateWorkflowAsync(Team team, WorkflowDefinition definition) =>
        (await SendOkAsync(HttpMethod.Post, "/api/workflows", team.OwnerId, team.TeamId, new { name = $"taint-{Guid.NewGuid():N}"[..14], definition, activations = Array.Empty<object>(), enabled = true })).GetProperty("id").GetGuid();

    /// <summary>One request through the real pipeline, as <paramref name="userId"/> in <paramref name="teamId"/>; asserts a 2xx and hands back the JSON body (an empty object for 204).</summary>
    private async Task<JsonElement> SendOkAsync(HttpMethod method, string url, Guid userId, Guid teamId, object? body = null)
    {
        var response = await SendAsync(method, url, userId, teamId, body);
        var text = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.ShouldBeTrue($"{method} {url} answered {(int)response.StatusCode}: {text}");

        return JsonDocument.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text).RootElement.Clone();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, Guid userId, Guid teamId, object? body = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = body is null ? null : JsonContent.Create(body) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());

        return await _factory.CreateClient().SendAsync(request);
    }

    /// <summary>Every run surface the SPA reads, fetched as the VIEWER and concatenated. The main reads must answer 200, so an empty body can never pass for "nothing leaked".</summary>
    private async Task<string> ViewerReadsAsync(Team team, Guid runId, Guid? childRunId, Guid agentRunId)
    {
        var childReads = childRunId is { } child ? new[] { $"/api/workflows/runs/{child}", $"/api/workflows/runs/{child}/records" } : [];
        var mustAnswer = new[]
        {
            $"/api/workflows/runs/{runId}", $"/api/workflows/runs/{runId}/records", $"/api/workflows/runs/{runId}/timeline",
            $"/api/agents/runs/{agentRunId}", $"/api/agents/runs/{agentRunId}/events",
        }.Concat(childReads);
        var reads = new List<string>();

        foreach (var url in mustAnswer)
        {
            var response = await SendAsync(HttpMethod.Get, url, team.ViewerId, team.TeamId);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"a Viewer reads {url}");
            reads.Add(await response.Content.ReadAsStringAsync());
        }

        reads.Add(await (await SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/pending-wait", team.ViewerId, team.TeamId)).Content.ReadAsStringAsync());

        return string.Join("\n", reads);
    }

    // ── State ────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<Team> SeedTeamAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var team = new Team(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());

        db.Team.Add(new Core.Persistence.Entities.Team { Id = team.TeamId, Slug = $"taint-{suffix}", Name = "Taint", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        foreach (var (userId, role) in new[] { (team.OwnerId, TeamRole.Owner), (team.ViewerId, TeamRole.Viewer) })
        {
            db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"taint-{role}-{suffix}@test.local", Name = $"Taint {role}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = team.TeamId, UserId = userId, Role = role, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        }

        await db.SaveChangesAsync();
        return team;
    }

    /// <summary>The run reached Success, and the ids of its agent run and its child run — the two runs the secrets were handed to.</summary>
    private async Task<(Guid AgentRunId, Guid ChildRunId)> AssertRanToSuccessAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(candidate => candidate.Id == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success, $"the whole chain ran to terminal; error: {run.Error}");

        var agentRunId = await db.AgentRun.AsNoTracking().Where(agent => agent.WorkflowRunId == runId).Select(agent => agent.Id).SingleAsync();
        var childRunId = await db.WorkflowRun.AsNoTracking().Where(child => child.ParentRunId == runId).Select(child => child.Id).SingleAsync();

        return (agentRunId, childRunId);
    }

    /// <summary>The run reached Success with its agent run reviewed and approved and its acceptance graded with stored evidence; the agent run's id and that evidence's artifact id.</summary>
    private async Task<(Guid AgentRunId, Guid EvidenceId)> AssertReviewedAndGradedAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var run = await db.WorkflowRun.AsNoTracking().SingleAsync(candidate => candidate.Id == runId);
        var agent = await db.AgentRun.AsNoTracking().SingleAsync(candidate => candidate.WorkflowRunId == runId);

        run.Status.ShouldBe(WorkflowRunStatus.Success, $"the run ran to terminal; error: {run.Error}; agent result: {agent.ResultJson}");
        agent.Status.ShouldBe(AgentRunStatus.Succeeded);
        var result = JsonDocument.Parse(agent.ResultJson!).RootElement;
        result.GetProperty("acceptancePassed").GetBoolean().ShouldBeTrue("the check passes only with the exact value");

        return (agent.Id, result.GetProperty("acceptanceEvidenceId").GetGuid());
    }

    private async Task<Guid> SeedReviewerModelAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var credentialId = Guid.NewGuid();
        var rowId = Guid.NewGuid();

        db.ModelCredential.Add(new ModelCredential { Id = credentialId, TeamId = teamId, Provider = DeterministicCriticLlmClient.ProviderTag, DisplayName = "reviewer", Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ModelCredentialModel.Add(new ModelCredentialModel { Id = rowId, ModelCredentialId = credentialId, ModelId = "critic-model", Source = ModelSource.Manual, Enabled = true });
        await db.SaveChangesAsync();

        return rowId;
    }

    /// <summary>The exact outputs of the child's trigger node, from the encrypted sidecar the engine writes only when it masked the public row.</summary>
    private async Task<IReadOnlyDictionary<string, JsonElement>> ChildStartOutputsAsync(Guid childRunId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var child = await db.WorkflowRun.AsNoTracking().SingleAsync(run => run.Id == childRunId);
        var recordId = await db.WorkflowRunRecord.AsNoTracking().Where(record => record.RunId == childRunId && record.NodeId == "start" && record.RecordType == WorkflowRunRecordTypes.NodeCompleted).Select(record => record.Id).SingleAsync();

        return (await scope.ServiceProvider.GetRequiredService<IWorkflowSensitivePayloadStore>().ReadNodeOutputsAsync(recordId, childRunId, child.TeamId, CancellationToken.None)).ShouldNotBeNull();
    }

    /// <summary>The launch host plus the deterministic output-review critic (provider <see cref="DeterministicCriticLlmClient.ProviderTag"/>), the only model fake these runs reach. A team that seeds no reviewer model never resolves it.</summary>
    public sealed class ReviewedApiFactory : TaskLaunchApiFactory
    {
        protected override void ConfigureContractTestServices(ContainerBuilder builder)
        {
            builder.RegisterType<CriticReviewScript>().AsSelf().SingleInstance();
            builder.RegisterType<DeterministicCriticLlmClient>().As<CodeSpace.Core.Services.Workflows.Llm.ILLMClient>().As<CodeSpace.Core.Services.Workflows.Llm.IStructuredLLMClient>().SingleInstance();
        }
    }

    private async Task<IReadOnlyList<string>> SweepAsync(Guid teamId, string secret)
    {
        using var scope = _factory.Services.CreateScope();
        return await SecretCanarySweep.FindAsync(_factory.ConnectionString, scope.ServiceProvider.GetRequiredService<IArtifactStore>(), scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>(), teamId, secret);
    }
}

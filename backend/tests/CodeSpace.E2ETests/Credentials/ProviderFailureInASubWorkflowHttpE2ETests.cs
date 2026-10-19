using System.Net;
using System.Text.Json;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using CodeSpace.Messages.Failures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity (HTTP surface) — a team authors a workflow that runs ANOTHER workflow as a sub-workflow, the child lists pull requests through a
/// GitLab connection whose OAuth grant is dead, the parent is run, and the PARENT's page is read the way the SPA reads it. Everything runs through the real
/// ASP.NET pipeline (JWT, the <c>X-Team-Id</c> scope, the controllers and their JSON options) over real Postgres: both workflows are saved with
/// <c>POST /api/workflows</c>, the parent is started with <c>POST /api/workflows/{id}/run</c>, and the real engine (the in-test deferred queue is the Hangfire
/// transport) parks the parent, runs the child against a loopback <see cref="GitLabDoorkeeperStub"/>, resumes the parent with the child's outcome and fails
/// it — then <c>GET /api/workflows/runs/{id}</c> answers as an Owner, a Member and a Viewer.
///
/// <para>The parent's page says what the child's does — the credential, the repository, each role's buttons — and names the child run it happened in, which the
/// page links to. Re-run on the parent is the parent's: it replays the PARENT, which starts a fresh child. A parent recorded before children carried their
/// failures answers exactly as it did, and a pointer to a run that is not the team's is never named.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ProviderFailureInASubWorkflowHttpE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private const string ChildName = "Review the pull requests";

    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public ProviderFailureInASubWorkflowHttpE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task A_parent_run_whose_child_ended_on_a_dead_connection_carries_the_childs_remedy_naming_the_child_run_for_an_owner_a_member_and_a_viewer()
    {
        using var gitlab = new GitLabDoorkeeperStub();   // never heard of the pair the row holds: the refresh is answered invalid_grant
        var world = await SeedWorldAsync(gitlab);
        var parentRunId = await RunParentAsync(world);
        var childRunId = await ChildRunOfAsync(world, parentRunId);

        foreach (var (asker, label, reconnect, relink) in new[] { (world.Owner, "owner", true, true), (world.Member, "member", false, false), (world.Viewer, "viewer", false, false) })
        {
            var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{parentRunId}", asker, world.Team);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{label}: {body}");

            var run = JsonDocument.Parse(body).RootElement;
            run.GetProperty("status").GetString().ShouldBe("Failure", $"{label}: fixture check: the child's failure ended the parent");
            run.GetProperty("error").GetString().ShouldBe("Node 'sub' failed.", $"{label}: the sentence is untouched");

            var remedy = run.GetProperty("remedy");
            remedy.GetProperty("code").GetString().ShouldBe("credential_needs_reconnect", label);
            remedy.GetProperty("resolved").GetBoolean().ShouldBeFalse(label);
            remedy.GetProperty("title").GetString().ShouldBe("GitLab no longer accepts the credential this run used", label);
            remedy.GetProperty("cause").GetString().ShouldStartWith($"This failed in the sub-workflow “{ChildName}” (run {childRunId.ToString()[..8]}), at node 'list'. ", Case.Sensitive, label);
            remedy.GetProperty("credential").GetProperty("id").GetGuid().ShouldBe(world.CredentialId, label);
            remedy.GetProperty("repositories")[0].GetProperty("fullPath").GetString().ShouldBe("acme/api", label);

            var origin = remedy.GetProperty("origin");
            origin.GetProperty("runId").GetGuid().ShouldBe(childRunId, $"{label}: the child run the page links to");
            origin.GetProperty("workflowId").GetGuid().ShouldBe(world.ChildWorkflowId, label);
            origin.GetProperty("workflowName").GetString().ShouldBe(ChildName, label);
            origin.GetProperty("nodeId").GetString().ShouldBe("list", label);

            var actions = remedy.GetProperty("actions").EnumerateArray().ToDictionary(a => a.GetProperty("kind").GetString()!, a => a);
            actions["reconnect"].GetProperty("enabled").GetBoolean().ShouldBe(reconnect, $"{label} reconnect");
            actions["relink"].GetProperty("enabled").GetBoolean().ShouldBe(relink, $"{label} relink");
            actions["open_providers"].GetProperty("enabled").GetBoolean().ShouldBeTrue(label);
            actions["rerun"].GetProperty("enabled").GetBoolean().ShouldBeFalse($"{label}: nothing is fixed yet");
        }

        var lean = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{parentRunId}/remedy", world.Viewer, world.Team);

        lean.StatusCode.ShouldBe(HttpStatusCode.OK, "the room reads the same remedy without the run detail around it");
        JsonDocument.Parse(await lean.Content.ReadAsStringAsync()).RootElement.GetProperty("origin").GetProperty("runId").GetGuid().ShouldBe(childRunId);

        // The child's own page says what happened there, and has nowhere else to point.
        var child = JsonDocument.Parse(await (await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{childRunId}", world.Owner, world.Team)).Content.ReadAsStringAsync()).RootElement;
        var childRemedy = child.GetProperty("remedy");

        childRemedy.GetProperty("code").GetString().ShouldBe("credential_needs_reconnect");
        (!childRemedy.TryGetProperty("origin", out var childOrigin) || childOrigin.ValueKind == JsonValueKind.Null).ShouldBeTrue("the failure happened in the child run itself");
        childRemedy.GetProperty("cause").GetString().ShouldNotContain("sub-workflow", Case.Insensitive);

        // The wire contract: `origin` is the only member this adds. Every member a client that predates it reads is still there, so an older SPA (and a mixed fleet's older API) loses nothing.
        var members = childRemedy.EnumerateObject().Select(p => p.Name).ToList();

        members.ShouldBeSubsetOf(new[] { "code", "title", "cause", "resolved", "credential", "repositories", "providerInstance", "redirectsTo", "origin", "actions", "askPeople" }, "no member beyond the one this adds");
        members.ShouldContain("code");
        members.ShouldContain("title");
        members.ShouldContain("cause");
        members.ShouldContain("resolved");
        members.ShouldContain("credential");
        members.ShouldContain("repositories");
        members.ShouldContain("actions");
        members.ShouldContain("askPeople");
    }

    [Fact]
    public async Task Re_running_the_parent_replays_the_parent_and_starts_a_fresh_child_while_the_original_child_is_left_as_it_was()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab);
        var parentRunId = await RunParentAsync(world);
        var firstChildRunId = await ChildRunOfAsync(world, parentRunId);

        var replay = await _tenants.SendAsync(HttpMethod.Post, $"/api/workflows/runs/{parentRunId}/replay", world.Owner, world.Team);
        var replayRunId = Guid.Parse(await ReadStringAsync(replay, "runId"));

        await _factory.JobClient.DrainAsync();

        replayRunId.ShouldNotBe(parentRunId, "the Re-run is a fresh run of the parent");

        var replayed = JsonDocument.Parse(await (await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{replayRunId}", world.Owner, world.Team)).Content.ReadAsStringAsync()).RootElement;
        var freshChildRunId = replayed.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("nodeId").GetString() == "sub").GetProperty("childRunId").GetGuid();

        freshChildRunId.ShouldNotBe(firstChildRunId, "the parent started a FRESH child, never reusing the failed one");
        replayed.GetProperty("status").GetString().ShouldBe("Failure", "the connection is still dead, so the fresh child fails on it again");
        replayed.GetProperty("remedy").GetProperty("origin").GetProperty("runId").GetGuid().ShouldBe(freshChildRunId, "and the replayed parent's page names the run it happened in this time");

        var original = JsonDocument.Parse(await (await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{parentRunId}", world.Owner, world.Team)).Content.ReadAsStringAsync()).RootElement;

        original.GetProperty("remedy").GetProperty("origin").GetProperty("runId").GetGuid().ShouldBe(firstChildRunId, "the original run still points at the child it had");
    }

    [Fact]
    public async Task A_parent_recorded_before_children_carried_their_failures_answers_exactly_as_it_did()
    {
        var world = await SeedWorldAsync(gitlab: null);
        var parentRunId = await SeedFailedParentAsync(world);

        using (var scope = _factory.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<IRunRecordLogger>();

            await logger.NodeStartedAsync(parentRunId, "sub", string.Empty, new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), CancellationToken.None);
            await logger.NodeFailedAsync(parentRunId, "sub", string.Empty, "Sub-workflow run failed: Node 'list' failed.", TimeSpan.FromMilliseconds(9), CancellationToken.None);
        }

        var detail = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{parentRunId}", world.Owner, world.Team);
        var run = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;

        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        run.GetProperty("error").GetString().ShouldBe("Node 'sub' failed.");
        (!run.TryGetProperty("remedy", out var remedy) || remedy.ValueKind == JsonValueKind.Null).ShouldBeTrue("the node recorded no failure, so nothing claims a remedy");
        (await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{parentRunId}/remedy", world.Owner, world.Team)).StatusCode.ShouldBe(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task A_pointer_to_a_run_that_is_not_the_teams_is_never_named_on_the_wire()
    {
        var world = await SeedWorldAsync(gitlab: null);
        var elsewhere = await SeedWorldAsync(gitlab: null, childName: "THE-OTHER-TEAMS-SECRET-WORKFLOW");
        var foreignRunId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, $"/api/workflows/{elsewhere.ChildWorkflowId}/run", elsewhere.Owner, elsewhere.Team, new { }), "runId"));
        var parentRunId = await SeedFailedParentAsync(world);

        await _factory.JobClient.DrainAsync();

        using (var scope = _factory.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<IRunRecordLogger>();
            var failure = ProviderFailureCapture.From(new CredentialNeedsReconnectException(world.CredentialId, "GitLab · NACHO.L", "invalid_grant"), world.RepositoryId)! with { Origin = new FailureOrigin { RunId = foreignRunId, NodeId = "list" } };

            await logger.NodeStartedAsync(parentRunId, "sub", string.Empty, new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), CancellationToken.None);
            await logger.NodeFailedAsync(parentRunId, "sub", string.Empty, "Sub-workflow run failed: Node 'list' failed.", TimeSpan.FromMilliseconds(9), failure, CancellationToken.None);
        }

        var detail = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{parentRunId}", world.Owner, world.Team);
        var body = await detail.Content.ReadAsStringAsync();

        detail.StatusCode.ShouldBe(HttpStatusCode.OK, body);
        body.ShouldNotContain("THE-OTHER-TEAMS-SECRET-WORKFLOW");
        body.ShouldNotContain(foreignRunId.ToString());

        var remedy = JsonDocument.Parse(body).RootElement.GetProperty("remedy");

        remedy.GetProperty("credential").GetProperty("id").GetGuid().ShouldBe(world.CredentialId, "the remedy is still the parent's own");
        (!remedy.TryGetProperty("origin", out var origin) || origin.ValueKind == JsonValueKind.Null).ShouldBeTrue("an origin that is not this team's run names nothing");

        // And the other team cannot read this team's parent at all.
        (await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{parentRunId}/remedy", elsewhere.Owner, elsewhere.Team)).StatusCode.ShouldBeOneOf(HttpStatusCode.NoContent, HttpStatusCode.NotFound);
    }

    // ─── Seeds ─────────────────────────────────────────────────────────────────

    private sealed record World(Guid Team, Guid Owner, Guid Member, Guid Viewer, Guid CredentialId, Guid RepositoryId, Guid ChildWorkflowId, Guid ParentWorkflowId);

    /// <summary>A team of an Owner, a Member and a Viewer; one GitLab connection — an OAuth grant due for renewal that the stub never issued, belonging to the Owner as a connect leaves it — and one repository running through it; a child workflow that lists its pull requests and a parent that runs it.</summary>
    private async Task<World> SeedWorldAsync(GitLabDoorkeeperStub? gitlab, string childName = ChildName)
    {
        var owner = await _tenants.AccountAsync();
        var member = await _tenants.AccountAsync();
        var viewer = await _tenants.AccountAsync();
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (member, TeamRole.Member), (viewer, TeamRole.Viewer));
        var baseUrl = gitlab?.BaseUrl ?? $"http://127.0.0.1:1/{Guid.NewGuid():N}";
        var instanceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var repositoryId = Guid.NewGuid();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
            var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
            var serializer = scope.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();
            var payload = new OAuthPayload { AccessToken = "at-0", RefreshToken = "rt-0", ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1) };

            db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = team, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = baseUrl, ApiUrl = baseUrl, OauthClientId = GitLabDoorkeeperStub.DefaultClientId, OauthClientSecretEnc = encryptor.Encrypt(GitLabDoorkeeperStub.DefaultClientSecret) });
            db.Credential.Add(new Credential { Id = credentialId, TeamId = team, ProviderInstanceId = instanceId, AuthType = AuthType.OAuth, Ownership = CredentialOwnership.Personal, OwnerUserId = owner, DisplayName = "GitLab · NACHO.L", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(payload)), ExpiresDate = payload.ExpiresAt, Status = CredentialStatus.Active });
            db.Repository.Add(new Repository { Id = repositoryId, TeamId = team, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = Random.Shared.Next(100_000, 999_999).ToString(), NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = $"{baseUrl}/acme/api", CloneUrlHttps = $"{baseUrl}/acme/api.git", Status = RepositoryStatus.Active });
            await db.SaveChangesAsync();
        }

        var childWorkflowId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/workflows", owner, team, ChildBody(repositoryId, childName)), "id"));
        var parentWorkflowId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/workflows", owner, team, ParentBody(childWorkflowId)), "id"));

        return new World(team, owner, member, viewer, credentialId, repositoryId, childWorkflowId, parentWorkflowId);
    }

    /// <summary>Starts the parent with <c>POST /api/workflows/{id}/run</c> and lets the engine walk it: parent parks, child fails on the dead connection, the child's completion resumes the parent, which fails.</summary>
    private async Task<Guid> RunParentAsync(World world)
    {
        var runId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, $"/api/workflows/{world.ParentWorkflowId}/run", world.Owner, world.Team, new { }), "runId"));

        await _factory.JobClient.DrainAsync();

        return runId;
    }

    private async Task<Guid> ChildRunOfAsync(World world, Guid parentRunId)
    {
        var detail = JsonDocument.Parse(await (await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{parentRunId}", world.Owner, world.Team)).Content.ReadAsStringAsync()).RootElement;

        return detail.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("nodeId").GetString() == "sub").GetProperty("childRunId").GetGuid();
    }

    /// <summary>A run of the team's that ended on the sub node, written the way the engine ends one — for a run whose ledger a test writes by hand.</summary>
    private async Task<Guid> SeedFailedParentAsync(World world)
    {
        var runId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _tenants.DbAsync(async db =>
        {
            db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = world.Team, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });
            db.WorkflowRun.Add(new WorkflowRun { Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = world.Team, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure, Error = "Node 'sub' failed.", ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            await db.SaveChangesAsync();
            return 0;
        });

        return runId;
    }

    // start → list (git.list_prs) → end. No error edge: the dead connection ends the run.
    private static object ChildBody(Guid repositoryId, string name) => new
    {
        name,
        definition = new
        {
            schemaVersion = 1,
            nodes = new object[]
            {
                new { id = "start", typeKey = "trigger.manual", config = new { }, inputs = new { } },
                new { id = "list", typeKey = "git.list_prs", config = new { }, inputs = new { repositoryId } },
                new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } },
            },
            edges = new object[] { new { from = "start", to = "list" }, new { from = "list", to = "end" } },
        },
        activations = Array.Empty<object>(),
        enabled = true,
    };

    // start → sub (flow.subworkflow: the child) → end.
    private static object ParentBody(Guid childWorkflowId) => new
    {
        name = $"parent-{Guid.NewGuid():N}"[..16],
        definition = new
        {
            schemaVersion = 1,
            nodes = new object[]
            {
                new { id = "start", typeKey = "trigger.manual", config = new { }, inputs = new { } },
                new { id = "sub", typeKey = "flow.subworkflow", config = new { workflowId = childWorkflowId }, inputs = new { inputs = new { } } },
                new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } },
            },
            edges = new object[] { new { from = "start", to = "sub" }, new { from = "sub", to = "end" } },
        },
        activations = Array.Empty<object>(),
        enabled = true,
    };

    private static async Task<string> ReadStringAsync(HttpResponseMessage response, string property)
    {
        var text = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.ShouldBeTrue($"{response.RequestMessage?.Method} {response.RequestMessage?.RequestUri?.AbsolutePath} answered {(int)response.StatusCode}: {text}");

        var value = JsonDocument.Parse(text).RootElement.GetProperty(property);

        return value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();
    }
}

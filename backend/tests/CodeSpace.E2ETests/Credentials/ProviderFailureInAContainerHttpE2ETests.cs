using System.Net;
using System.Text.Json;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.Messages.Credentials;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity (HTTP surface) — a team authors a workflow whose <c>flow.map</c> (or <c>flow.loop</c>) body lists pull requests
/// through a GitLab connection whose OAuth grant is dead, runs it, and reads the run the way the SPA does. Everything runs through the
/// real ASP.NET pipeline (JWT, the <c>X-Team-Id</c> scope, the controllers and their JSON options) over real Postgres: the workflow is
/// saved with <c>POST /api/workflows</c>, started with <c>POST /api/workflows/{id}/run</c>, executed by the real engine (the in-test
/// deferred queue is the Hangfire transport) calling the real GitLab provider and credential refresher against a loopback
/// <see cref="GitLabDoorkeeperStub"/>, and then read with <c>GET /api/workflows/runs/{id}</c> as an Owner, a Member and a Viewer.
///
/// <para>The run that a container ended says what a run that a top-level node ended does: the remedy names the credential and the
/// repository, each role is offered what the endpoint behind the button would accept, and the sentence the run always carried is
/// untouched. A run recorded BEFORE a container carried the failure — its body node recorded one, the container did not — answers
/// exactly as it did: the same sentence, no remedy, and an empty lean read. A map-branch rerun (<c>POST …/rerun-map-branch</c>) forks the run and
/// replays the failed branch it did not choose: the fork fails on it again and answers with the remedy the run it forked did.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ProviderFailureInAContainerHttpE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public ProviderFailureInAContainerHttpE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Theory]
    [InlineData("map")]
    [InlineData("loop")]
    public async Task A_run_a_container_ended_on_a_dead_connection_carries_the_remedy_a_top_level_node_would_for_an_owner_a_member_and_a_viewer(string container)
    {
        using var gitlab = new GitLabDoorkeeperStub();   // never heard of the pair the row holds: the refresh is answered invalid_grant
        var world = await SeedWorldAsync(gitlab);

        var workflowId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/workflows", world.Owner, world.Team, WorkflowBody(container, world.RepositoryId)), "id"));
        var runId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/run", world.Owner, world.Team, new { }), "runId"));

        await _factory.JobClient.DrainAsync();

        foreach (var (asker, label, reconnect, relink) in new[] { (world.Owner, "owner", true, true), (world.Member, "member", false, false), (world.Viewer, "viewer", false, false) })
        {
            var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}", asker, world.Team);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{label}: {body}");

            var run = JsonDocument.Parse(body).RootElement;
            run.GetProperty("status").GetString().ShouldBe("Failure", $"{label}: fixture check: the dead connection ended the container, which ended the run");
            run.GetProperty("error").GetString().ShouldBe($"Node '{container}' failed.", $"{label}: the sentence is untouched");

            var remedy = run.GetProperty("remedy");
            remedy.GetProperty("code").GetString().ShouldBe("credential_needs_reconnect", label);
            remedy.GetProperty("resolved").GetBoolean().ShouldBeFalse(label);
            remedy.GetProperty("title").GetString().ShouldBe("GitLab no longer accepts the credential this run used", label);
            remedy.GetProperty("credential").GetProperty("id").GetGuid().ShouldBe(world.CredentialId, label);
            remedy.GetProperty("credential").GetProperty("displayName").GetString().ShouldBe("GitLab · NACHO.L", label);
            remedy.GetProperty("repositories")[0].GetProperty("fullPath").GetString().ShouldBe("acme/api", label);

            var actions = remedy.GetProperty("actions").EnumerateArray().ToDictionary(a => a.GetProperty("kind").GetString()!, a => a);
            actions["reconnect"].GetProperty("enabled").GetBoolean().ShouldBe(reconnect, $"{label} reconnect");
            actions["relink"].GetProperty("enabled").GetBoolean().ShouldBe(relink, $"{label} relink");
            actions["open_providers"].GetProperty("enabled").GetBoolean().ShouldBeTrue(label);
            actions["rerun"].GetProperty("enabled").GetBoolean().ShouldBeFalse($"{label}: nothing is fixed yet");
        }

        var lean = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/remedy", world.Viewer, world.Team);

        lean.StatusCode.ShouldBe(HttpStatusCode.OK, "the room reads the same remedy without the run detail around it");
        JsonDocument.Parse(await lean.Content.ReadAsStringAsync()).RootElement.GetProperty("credential").GetProperty("id").GetGuid().ShouldBe(world.CredentialId);
    }

    [Fact]
    public async Task A_map_branch_rerun_fork_that_fails_on_a_replayed_failed_branch_carries_the_remedy_of_the_run_it_forked()
    {
        using var gitlab = new GitLabDoorkeeperStub();
        var world = await SeedWorldAsync(gitlab);

        // Two elements through the one dead connection: the map fails on element 0, the first branch by element order. Re-running element 1 replays that failed branch on the fork.
        var workflowId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, "/api/workflows", world.Owner, world.Team, WorkflowBody("map", world.RepositoryId, elements: 2)), "id"));
        var runId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/run", world.Owner, world.Team, new { }), "runId"));

        await _factory.JobClient.DrainAsync();

        var forkId = Guid.Parse(await ReadStringAsync(await _tenants.SendAsync(HttpMethod.Post, $"/api/workflows/runs/{runId}/rerun-map-branch", world.Owner, world.Team, new { mapNodeId = "map", branchIndex = 1 }), "runId"));

        await _factory.JobClient.DrainAsync();

        var detail = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{forkId}", world.Owner, world.Team);
        var body = await detail.Content.ReadAsStringAsync();

        detail.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        var fork = JsonDocument.Parse(body).RootElement;

        fork.GetProperty("parentRunId").GetGuid().ShouldBe(runId, "fixture check: this is the fork of the run that failed");
        fork.GetProperty("status").GetString().ShouldBe("Failure", "fixture check: the replayed branch failed, so the map fails again");
        fork.GetProperty("error").GetString().ShouldBe("Node 'map' failed.", "the sentence is untouched");

        var remedy = fork.GetProperty("remedy");

        remedy.GetProperty("code").GetString().ShouldBe("credential_needs_reconnect", "the fork answers for the replayed branch as the run it forked did");
        remedy.GetProperty("credential").GetProperty("id").GetGuid().ShouldBe(world.CredentialId);
        remedy.GetProperty("repositories")[0].GetProperty("fullPath").GetString().ShouldBe("acme/api");
    }

    [Fact]
    public async Task A_container_run_recorded_before_the_container_carried_the_failure_answers_exactly_as_it_did()
    {
        var world = await SeedWorldAsync(gitlab: null);
        var runId = Guid.NewGuid();
        var requestId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _tenants.DbAsync(async db =>
        {
            db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = world.Team, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });
            db.WorkflowRun.Add(new WorkflowRun { Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = world.Team, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure, Error = "Node 'map' failed.", ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            await db.SaveChangesAsync();
            return 0;
        });

        // What a run that failed before containers carried failures left: the body node recorded the failure, the map recorded a sentence alone.
        using (var scope = _factory.Services.CreateScope())
        {
            var logger = scope.ServiceProvider.GetRequiredService<IRunRecordLogger>();
            var thrown = new CredentialNeedsReconnectException(world.CredentialId, "GitLab · NACHO.L", "invalid_grant");

            await logger.NodeStartedAsync(runId, "map", string.Empty, new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), CancellationToken.None);
            await logger.NodeFailedAsync(runId, "list", "map#0", thrown.Message, TimeSpan.FromMilliseconds(4), ProviderFailureCapture.From(thrown, world.RepositoryId), CancellationToken.None);
            await logger.NodeFailedAsync(runId, "map", string.Empty, "Node 'list' in map 'map' failed.", TimeSpan.FromMilliseconds(9), CancellationToken.None);
        }

        var detail = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}", world.Owner, world.Team);
        var run = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;

        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        run.GetProperty("error").GetString().ShouldBe("Node 'map' failed.");
        (!run.TryGetProperty("remedy", out var remedy) || remedy.ValueKind == JsonValueKind.Null).ShouldBeTrue("the container recorded no failure, so nothing claims a remedy for a run that ended on it");
        (await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/remedy", world.Owner, world.Team)).StatusCode.ShouldBe(HttpStatusCode.NoContent, "no remedy is an empty answer, not an error");
    }

    // ─── Seeds ─────────────────────────────────────────────────────────────────

    private sealed record World(Guid Team, Guid Owner, Guid Member, Guid Viewer, Guid CredentialId, Guid RepositoryId);

    /// <summary>A team of an Owner, a Member and a Viewer; one GitLab connection — an OAuth grant due for renewal that the stub never issued — and one repository running through it.</summary>
    private async Task<World> SeedWorldAsync(GitLabDoorkeeperStub? gitlab)
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

        return new World(team, owner, member, viewer, credentialId, repositoryId);
    }

    // start → <container>(body: <start> → list) → end. `list` is the node the dead connection fails; no error edge anywhere, so it ends the container, which ends the run.
    // A map runs one branch per element (`elements` of them, each the repository); a loop is given the repository itself.
    private static object WorkflowBody(string container, Guid repositoryId, int elements = 1) => new
    {
        name = $"container-{Guid.NewGuid():N}"[..20],
        definition = new
        {
            schemaVersion = 1,
            nodes = container == "map"
                ? new object[]
                {
                    new { id = "start", typeKey = "trigger.manual", config = new { }, inputs = new { } },
                    new { id = "map", typeKey = "flow.map", config = new { }, inputs = new { items = Enumerable.Repeat(repositoryId, elements).ToArray() } },
                    new { id = "ms", typeKey = "flow.map_start", parentId = "map", config = new { }, inputs = new { } },
                    new { id = "list", typeKey = "git.list_prs", parentId = "map", config = new { }, inputs = new { repositoryId = "{{item}}" } },
                    new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } },
                }
                : new object[]
                {
                    new { id = "start", typeKey = "trigger.manual", config = new { }, inputs = new { } },
                    new { id = "loop", typeKey = "flow.loop", config = new { maxIterations = 2 }, inputs = new { } },
                    new { id = "ls", typeKey = "flow.loop_start", parentId = "loop", config = new { }, inputs = new { } },
                    new { id = "list", typeKey = "git.list_prs", parentId = "loop", config = new { }, inputs = new { repositoryId } },
                    new { id = "end", typeKey = "builtin.terminal", config = new { }, inputs = new { } },
                },
            edges = new object[] { new { from = "start", to = container }, new { from = container, to = "end" }, new { from = container == "map" ? "ms" : "ls", to = "list" } },
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

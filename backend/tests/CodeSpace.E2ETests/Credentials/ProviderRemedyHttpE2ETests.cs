using System.Net;
using System.Text.Json;
using CodeSpace.Core.Failures;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Workflows.Lifecycle;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Credentials;

/// <summary>
/// 🟢 High fidelity (HTTP surface) — what the run detail, the lean remedy read, the credential list and a repository's refused
/// deliveries answer over the wire to an Owner, a Member and a Viewer of one team. The real ASP.NET pipeline (JWT, the
/// <c>X-Team-Id</c> scope, the authorization behaviors, the controllers and their JSON options) over real Postgres. The failures
/// are written into the ledger by the real <see cref="IRunRecordLogger"/> from the real exceptions through the real capture, so the
/// wire shape is the one the engine's own records produce; the engine's capture itself is pinned at the integration tier
/// (<c>ProviderFailureThroughTheEngineFlowTests</c>, with a real GitLab stub), which an HTTP test would only repeat slowly.
///
/// <para>Also pinned here: a button is enabled exactly when the endpoint behind it accepts the request (the credential's own Member is
/// offered no Reconnect, and the OAuth init answers them 403), a standing lookup the provider did not answer blames nobody while a row
/// written before the cause was kept keeps the repair, a run recorded before failures were kept answers exactly as it did (no
/// <c>remedy</c>, the same <c>error</c>), and another team's run answers nothing.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class ProviderRemedyHttpE2ETests : IClassFixture<TaskLaunchApiFactory>
{
    private readonly TaskLaunchApiFactory _factory;
    private readonly TenantHarness _tenants;

    public ProviderRemedyHttpE2ETests(TaskLaunchApiFactory factory)
    {
        _factory = factory;
        _tenants = new TenantHarness(factory);
    }

    [Fact]
    public async Task The_run_detail_carries_a_remedy_computed_for_whoever_asks_an_owner_a_member_and_a_viewer()
    {
        var world = await SeedWorldAsync();
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"), recordFailure: true);

        foreach (var (asker, label, reconnect, relink) in new[] { (world.Owner, "owner", true, true), (world.Member, "member", false, false), (world.Viewer, "viewer", false, false) })
        {
            var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}", asker, world.Team);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.ShouldBe(HttpStatusCode.OK, $"{label}: {body}");

            var run = JsonDocument.Parse(body).RootElement;
            run.GetProperty("error").GetString().ShouldBe("Node 'list' failed.", $"{label}: the sentence is untouched");

            var remedy = run.GetProperty("remedy");
            remedy.GetProperty("code").GetString().ShouldBe("credential_needs_reconnect", label);
            remedy.GetProperty("resolved").GetBoolean().ShouldBeFalse(label);
            remedy.GetProperty("title").GetString().ShouldBe("The provider no longer accepts the credential this run used".Replace("The provider", "GitLab"), label);
            remedy.GetProperty("credential").GetProperty("displayName").GetString().ShouldBe("GitLab · nacho", label);
            remedy.GetProperty("credential").GetProperty("health").GetString().ShouldBe("rejected", $"{label}: enum names are the lower-case ones a client branches on");
            remedy.GetProperty("repositories")[0].GetProperty("fullPath").GetString().ShouldBe("acme/api", label);

            var actions = remedy.GetProperty("actions").EnumerateArray().ToDictionary(a => a.GetProperty("kind").GetString()!, a => a);
            actions["reconnect"].GetProperty("enabled").GetBoolean().ShouldBe(reconnect, $"{label} reconnect");
            actions["relink"].GetProperty("enabled").GetBoolean().ShouldBe(relink, $"{label} relink");
            actions["open_providers"].GetProperty("enabled").GetBoolean().ShouldBeTrue(label);
            actions["rerun"].GetProperty("enabled").GetBoolean().ShouldBeFalse($"{label}: nothing is fixed yet");

            if (!reconnect)
            {
                actions["relink"].GetProperty("disabledReason").GetString().ShouldBe("Needs the Admin or Owner role.", label);
                remedy.GetProperty("askPeople").EnumerateArray().Select(p => p.GetProperty("role").GetString()).ShouldContain("Owner", $"{label}: told who can");
            }
            else
            {
                remedy.GetProperty("askPeople").GetArrayLength().ShouldBe(0, $"{label}: nobody to ask");
            }
        }
    }

    [Fact]
    public async Task A_button_is_enabled_exactly_when_the_endpoint_behind_it_accepts_the_request_for_the_credentials_own_member()
    {
        var world = await SeedWorldAsync();
        var runId = await SeedFailedRunAsync(world, new CredentialNeedsReconnectException(world.CredentialId, "GitLab · nacho", "invalid_grant"), recordFailure: true);

        var detail = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}", world.CredentialOwner, world.Team);
        var actions = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement.GetProperty("remedy").GetProperty("actions").EnumerateArray().ToDictionary(a => a.GetProperty("kind").GetString()!, a => a.GetProperty("enabled").GetBoolean());

        var init = new { providerInstanceId = world.InstanceId, displayName = "GitLab · nacho", replaceCredentialId = world.CredentialId };
        var asTheCredentialsPerson = await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/oauth/init", world.CredentialOwner, world.Team, init);
        var asTheOwner = await _tenants.SendAsync(HttpMethod.Post, "/api/credentials/oauth/init", world.Owner, world.Team, init);

        actions["reconnect"].ShouldBeFalse("the card must not offer the credential's own Member a button …");
        asTheCredentialsPerson.StatusCode.ShouldBe(HttpStatusCode.Forbidden, "… that this endpoint answers 403: it needs credentials.manage, which owning the row does not give");
        asTheOwner.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden, "the team Owner passes authorization (the stub instance has no OAuth client, which is a different refusal)");
    }

    [Fact]
    public async Task The_lean_remedy_read_answers_the_same_remedy_without_the_run_detail_around_it()
    {
        var world = await SeedWorldAsync();
        var runId = await SeedFailedRunAsync(world, new CredentialDisconnectedException(world.CredentialId, "GitLab · nacho"), recordFailure: true);

        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/remedy", world.Viewer, world.Team);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        var remedy = JsonDocument.Parse(body).RootElement;
        remedy.GetProperty("code").GetString().ShouldBe("credential_disconnected");
        remedy.EnumerateObject().Select(p => p.Name).ShouldNotContain("nodes", "this is the remedy alone, not a run detail");
        remedy.GetProperty("actions").EnumerateArray().Where(a => a.GetProperty("enabled").GetBoolean()).Select(a => a.GetProperty("kind").GetString()).ShouldBe(new[] { "open_providers" }, "a Viewer may look at the connections and nothing else");
    }

    [Fact]
    public async Task A_run_recorded_before_failures_were_kept_answers_exactly_as_it_did()
    {
        var world = await SeedWorldAsync();
        var runId = await SeedFailedRunAsync(world, new CredentialDisconnectedException(world.CredentialId, "GitLab · nacho"), recordFailure: false);

        var detail = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}", world.Owner, world.Team);
        var run = JsonDocument.Parse(await detail.Content.ReadAsStringAsync()).RootElement;

        detail.StatusCode.ShouldBe(HttpStatusCode.OK);
        run.GetProperty("error").GetString().ShouldBe("Node 'list' failed.");
        (!run.TryGetProperty("remedy", out var remedy) || remedy.ValueKind == JsonValueKind.Null).ShouldBeTrue("nothing recorded the code, so nothing claims a remedy");
        run.GetProperty("nodes")[0].GetProperty("error").GetString().ShouldContain("was disconnected", customMessage: "the node still carries the sentence it always did");

        var lean = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/remedy", world.Owner, world.Team);

        lean.StatusCode.ShouldBe(HttpStatusCode.NoContent, "no remedy is an empty answer, not an error");
    }

    [Fact]
    public async Task Another_teams_run_answers_nothing_to_a_stranger()
    {
        var world = await SeedWorldAsync();
        var runId = await SeedFailedRunAsync(world, new CredentialDisconnectedException(world.CredentialId, "GitLab · nacho"), recordFailure: true);
        var stranger = await _tenants.AccountAsync();
        var otherTeam = await _tenants.TeamAsync((stranger, TeamRole.Owner));

        var lean = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}/remedy", stranger, otherTeam);
        var detail = await _tenants.SendAsync(HttpMethod.Get, $"/api/workflows/runs/{runId}", stranger, otherTeam);

        lean.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        detail.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task The_credential_list_returns_a_disconnected_credential_with_the_repositories_still_bound_to_it()
    {
        var world = await SeedWorldAsync();
        await _tenants.DbAsync(async db =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE credential SET status = 'Revoked' WHERE id = {world.CredentialId}");
            return 0;
        });

        var response = await _tenants.SendAsync(HttpMethod.Get, "/api/credentials", world.Member, world.Team);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        var credential = JsonDocument.Parse(body).RootElement.EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == world.CredentialId);
        credential.GetProperty("status").GetString().ShouldBe("Revoked");
        credential.GetProperty("health").GetString().ShouldBe("revoked");
        credential.GetProperty("boundRepositoryCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_repositorys_refused_deliveries_carry_the_remedy_for_the_ones_that_blame_its_credential()
    {
        var world = await SeedWorldAsync();
        await _tenants.DbAsync(async db =>
        {
            db.WorkflowRunRequest.Add(new WorkflowRunRequest
            {
                Id = Guid.NewGuid(), TeamId = world.Team, RepositoryId = world.RepositoryId, SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}gitlab", ExternalEventId = Guid.NewGuid().ToString("N"), ActorType = WorkflowRunActorTypes.Webhook,
                Status = WorkflowRunRequestStatus.Rejected, Error = $"{WorkflowRunRequestRejectionReasons.CredentialRejected}: GitLab answered HTTP 401 to the repository's credential 'GitLab · nacho'", ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            });
            await db.SaveChangesAsync();
            return 0;
        });

        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories/{world.RepositoryId}/rejected-deliveries", world.Member, world.Team);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        var list = JsonDocument.Parse(body).RootElement;
        list.GetProperty("deliveries")[0].GetProperty("reason").GetString().ShouldBe("credential_rejected");

        var remedy = list.GetProperty("remedy");
        remedy.GetProperty("code").GetString().ShouldBe("credential_rejected");
        remedy.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ShouldNotContain("rerun", "a refused delivery started no run");
    }

    [Fact]
    public async Task A_standing_lookup_the_provider_did_not_answer_carries_a_remedy_that_blames_nobody_and_a_row_without_the_cause_still_offers_the_repair()
    {
        var world = await SeedWorldAsync();
        var lookup = CodeSpace.Core.Services.Workflows.Failures.PersistedFailureJson.AsPayload(new CodeSpace.Messages.Failures.PersistedFailure { Code = WorkflowRunRequestRejectionReasons.AuthorStandingUnknown, Details = new Dictionary<string, string> { [StandingLookupCauses.DetailKey] = StandingLookupCauses.NoAnswer, ["providerStatus"] = "503" } });
        await SeedStandingRefusalAsync(world, "GitLab answered HTTP 503 to the lookup", lookup, minutesAgo: 1);

        var answered = await DeliveriesRemedyAsync(world);

        answered.GetProperty("code").GetString().ShouldBe("author_standing_unknown");
        answered.GetProperty("cause").GetString().ShouldContain("not a verdict on");
        answered.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ShouldBe(new[] { "open_providers" }, "a provider that did not answer is nobody's credential to repair");

        // The same refusal as a row written before the cause was kept: unchanged — either is possible, so the repair stays.
        var older = await SeedWorldAsync();
        await SeedStandingRefusalAsync(older, "GitLab refused the lookup (HTTP 403)", metadata: null, minutesAgo: 1);

        var unknown = await DeliveriesRemedyAsync(older);

        unknown.GetProperty("cause").GetString().ShouldContain("can also be a timeout or an error");
        unknown.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ShouldContain("reconnect");
    }

    // ─── Seeds ─────────────────────────────────────────────────────────────────

    private async Task SeedStandingRefusalAsync(World world, string detail, string? metadata, int minutesAgo)
    {
        await _tenants.DbAsync(async db =>
        {
            db.WorkflowRunRequest.Add(new WorkflowRunRequest
            {
                Id = Guid.NewGuid(), TeamId = world.Team, RepositoryId = world.RepositoryId, SourceType = $"{WorkflowRunSourceTypes.ProviderPrefix}gitlab", ExternalEventId = Guid.NewGuid().ToString("N"), ActorType = WorkflowRunActorTypes.Webhook,
                Status = WorkflowRunRequestStatus.Rejected, Error = $"{WorkflowRunRequestRejectionReasons.AuthorStandingUnknown}: {detail}", ReceivedAt = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo), RequestMetadataJson = metadata ?? "{}",
            });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    private async Task<JsonElement> DeliveriesRemedyAsync(World world)
    {
        var response = await _tenants.SendAsync(HttpMethod.Get, $"/api/repositories/{world.RepositoryId}/rejected-deliveries", world.Owner, world.Team);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK, body);

        return JsonDocument.Parse(body).RootElement.GetProperty("remedy").Clone();
    }

    private sealed record World(Guid Team, Guid Owner, Guid Member, Guid Viewer, Guid CredentialOwner, Guid InstanceId, Guid CredentialId, Guid RepositoryId);

    /// <summary>A team of an Owner, a Member and a Viewer; one GitLab connection owned by a fourth person; one repository running through it.</summary>
    private async Task<World> SeedWorldAsync()
    {
        var owner = await _tenants.AccountAsync();
        var member = await _tenants.AccountAsync();
        var viewer = await _tenants.AccountAsync();
        var nacho = await _tenants.AccountAsync(name: $"Nacho {Guid.NewGuid():N}"[..14]);
        var team = await _tenants.TeamAsync((owner, TeamRole.Owner), (member, TeamRole.Member), (viewer, TeamRole.Viewer), (nacho, TeamRole.Member));
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var instance = new ProviderInstance { Id = Guid.NewGuid(), TeamId = team, Provider = ProviderKind.GitLab, DisplayName = "gitlab", BaseUrl = $"http://127.0.0.1:1/{suffix}", ApiUrl = $"http://127.0.0.1:1/{suffix}" };
        var credential = new Credential { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, OwnerUserId = nacho, Ownership = CredentialOwnership.Personal, AuthType = AuthType.OAuth, DisplayName = "GitLab · nacho", EncryptedPayload = "x", Status = CredentialStatus.Error, LastError = "needs reconnect: invalid_grant" };
        var repository = new Repository { Id = Guid.NewGuid(), TeamId = team, ProviderInstanceId = instance.Id, CredentialId = credential.Id, ExternalId = Random.Shared.Next(100_000, 999_999).ToString(), NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main", Visibility = RepositoryVisibility.Private, WebUrl = "http://127.0.0.1:1/acme/api", Status = RepositoryStatus.Error };

        await _tenants.DbAsync(async db =>
        {
            db.ProviderInstance.Add(instance);
            db.Credential.Add(credential);
            db.Repository.Add(repository);
            await db.SaveChangesAsync();
            return 0;
        });

        return new World(team, owner, member, viewer, nacho, instance.Id, credential.Id, repository.Id);
    }

    /// <summary>A failed run whose node failed on the exception, recorded by the real ledger writer — with the machine-readable failure when <paramref name="recordFailure"/>, as a run recorded before they were kept otherwise.</summary>
    private async Task<Guid> SeedFailedRunAsync(World world, Exception thrown, bool recordFailure)
    {
        var requestId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        await _tenants.DbAsync(async db =>
        {
            db.WorkflowRunRequest.Add(new WorkflowRunRequest { Id = requestId, TeamId = world.Team, WorkflowId = null, SourceType = WorkflowRunSourceTypes.Snapshot, ActorType = "user", ActorId = SystemUsers.SeederId, NormalizedPayloadJson = "{}", Status = WorkflowRunRequestStatus.Consumed, ReceivedAt = now, VerifiedAt = now, NormalizedAt = now });
            db.WorkflowRun.Add(new WorkflowRun { Id = runId, WorkflowId = null, WorkflowVersion = null, TeamId = world.Team, RunRequestId = requestId, SourceType = WorkflowRunSourceTypes.Snapshot, Status = WorkflowRunStatus.Failure, Error = "Node 'list' failed.", ScopeRepositoryIds = [], ScopeProjectIds = [], CreatedDate = now, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
            await db.SaveChangesAsync();
            return 0;
        });

        using var scope = _factory.Services.CreateScope();
        var logger = scope.ServiceProvider.GetRequiredService<IRunRecordLogger>();

        await logger.NodeStartedAsync(runId, "list", string.Empty, new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(), CancellationToken.None);
        await logger.NodeFailedAsync(runId, "list", string.Empty, thrown.Message, TimeSpan.FromMilliseconds(4), recordFailure ? ProviderFailureCapture.From(thrown, world.RepositoryId) : null, CancellationToken.None);

        return runId;
    }
}

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Credentials;
using CodeSpace.Messages.Dtos.Workflows;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Webhooks;

/// <summary>
/// Who a provider delivery is allowed to reach, on the WIRE. A signed delivery for one team's repository
/// must start runs in that team and nowhere else — not in another tenant's workflow whose trigger names
/// no repository, not through a group hook whose owner path does not cover the repository, and not
/// through a hook the team has retired. And one activation that refuses to launch must cost only its
/// own run, never the delivery for everyone else. The owner-path rule must not cost events either: a
/// repository whose owner moved at the provider routes again once it is re-synced.
///
/// <para>These are HTTP facts because the failure modes were HTTP facts: the leak was a 200 to a
/// Personal-team user who read another tenant's private PR over <c>GET /api/workflows/runs/{id}</c>, and
/// the poison was a 404 every provider read as "this hook is gone". Only the real host, JWT, X-Team-Id
/// scope, MediatR transaction and the webhook controller's status mapping can show either.</para>
///
/// <para>Tier: 🟢 High-fidelity — real app host (<see cref="WebhookApiFactory"/>), real Postgres, real
/// signature verifiers and normalizers, real <c>RunSourceDispatcher</c> and <c>RunStarter</c>. Only the
/// Hangfire client is a no-op, so a dispatched run is a row and never executes; the owner-move tests also
/// answer the real GitLab provider class (NGitLab, over the wire) from a loopback <see cref="StubProviderHost"/>.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class WebhookTenancyE2ETests : IClassFixture<WebhookApiFactory>
{
    private readonly WebhookApiFactory _factory;

    public WebhookTenancyE2ETests(WebhookApiFactory factory) { _factory = factory; }

    [Fact]
    public async Task A_foreign_teams_catch_all_activation_receives_no_run_and_no_payload()
    {
        var owner = await SeedTeamWithRepositoryHookAsync(ProviderKind.GitHub, "acme-a/private-repo");
        var outsider = await SeedPersonalOnlyUserAsync();
        var marker = $"TEAM-A-PRIVATE-{Guid.NewGuid():N}";

        var foreignPr = await CreateWorkflowAsync(outsider.UserId, outsider.TeamId, WorkflowBody("trigger.pr.opened", "{}"));
        var foreignPush = await CreateWorkflowAsync(outsider.UserId, outsider.TeamId, WorkflowBody("trigger.push", """{"branches":["main"]}"""));
        var ownPr = await CreateWorkflowAsync(owner.UserId, owner.TeamId, WorkflowBody("trigger.pr.opened", "{}"));

        var prStatus = await PostGitHubAsync(owner, "pull_request", GitHubPrBody("opened", marker));
        var pushStatus = await PostGitHubAsync(owner, "push", GitHubPushBody(marker));

        prStatus.ShouldBe(HttpStatusCode.OK, customMessage: "the owning team's delivery must still be accepted");
        pushStatus.ShouldBe(HttpStatusCode.OK, customMessage: "the owning team's delivery must still be accepted");

        (await CountRunsAsync(foreignPr)).ShouldBe(0, customMessage: "A {} PR trigger in another tenant's Personal team started a run from this team's private repository. Check RunSourceDispatcher.LoadActiveActivationsAsync filters on the event repository's team.");
        (await CountRunsAsync(foreignPush)).ShouldBe(0, customMessage: "A branch-only push trigger in another tenant's team started a run from this team's private repository.");
        (await CountRunsAsync(ownPr)).ShouldBe(1, customMessage: "{} must still mean 'any repository of THIS team' — the owning team's catch-all must fire.");

        (await LoadRequestPayloadsAsync(outsider.TeamId)).ShouldNotContain(p => p.Contains(marker), customMessage: "no row in the outsider's team may carry the private payload — check workflow_run_request.normalized_payload_json for the marker.");
    }

    [Fact]
    public async Task A_save_refuses_a_trigger_naming_another_teams_repository_and_an_enforced_opt_in_its_graph_cannot_honour()
    {
        var owner = await SeedTeamWithRepositoryHookAsync(ProviderKind.GitHub, "acme-b/private-repo");
        var outsider = await SeedPersonalOnlyUserAsync();

        var foreignRepository = JsonSerializer.Serialize(new { repositories = new[] { new { repositoryId = owner.RepositoryId } } });
        var foreignSave = await SendWorkflowAsync(outsider.UserId, outsider.TeamId, WorkflowBody("trigger.pr.opened", foreignRepository));
        var poisonSave = await SendWorkflowAsync(outsider.UserId, outsider.TeamId, WorkflowBody("trigger.pr.opened", "{}", completionMode: "enforced"));
        var ownSave = await SendWorkflowAsync(owner.UserId, owner.TeamId, WorkflowBody("trigger.pr.opened", foreignRepository));

        foreignSave.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, customMessage: $"an activation naming a repository the team does not hold must never be stored. Body: {foreignSave.Body}");
        foreignSave.Body.ShouldContain(owner.RepositoryId.ToString());
        poisonSave.Status.ShouldBe(HttpStatusCode.UnprocessableEntity, customMessage: $"'enforced' on a graph whose mode is not Enforceable refuses at every launch — it must be refused at save instead. Body: {poisonSave.Body}");
        poisonSave.Body.ShouldContain("generic");
        ownSave.Status.ShouldBe(HttpStatusCode.OK, customMessage: $"the owning team naming its own repository is the ordinary case. Body: {ownSave.Body}");
    }

    [Fact]
    public async Task A_refusing_activation_is_audited_and_neither_its_siblings_nor_other_teams_lose_the_delivery()
    {
        var github = await SeedTeamWithRepositoryHookAsync(ProviderKind.GitHub, "victim/gh");
        var gitlab = await SeedTeamWithRepositoryHookAsync(ProviderKind.GitLab, "victim2/gl");
        var outsider = await SeedPersonalOnlyUserAsync();

        var githubHealthy = await CreateWorkflowAsync(github.UserId, github.TeamId, WorkflowBody("trigger.pr.opened", ScopedTo(github.RepositoryId)));
        var gitlabHealthy = await CreateWorkflowAsync(gitlab.UserId, gitlab.TeamId, WorkflowBody("trigger.pr.opened", ScopedTo(gitlab.RepositoryId)));
        var siblingPoison = await CreateWorkflowAsync(github.UserId, github.TeamId, WorkflowBody("trigger.pr.opened", "{}"));
        var foreignPoison = await CreateWorkflowAsync(outsider.UserId, outsider.TeamId, WorkflowBody("trigger.pr.opened", "{}"));

        // Rows stored before the save-time gate existed: the launch-time backstop is what still refuses them.
        await AppendLegacyEnforcedVersionAsync(siblingPoison);
        await AppendLegacyEnforcedVersionAsync(foreignPoison);

        var deliveryId = $"d-{Guid.NewGuid():N}";
        var githubStatus = await PostGitHubAsync(github, "pull_request", GitHubPrBody("opened", "victim"), deliveryId);
        var gitlabStatus = await PostGitLabAsync(gitlab, GitLabMergeRequestBody());

        githubStatus.ShouldBe(HttpStatusCode.OK, customMessage: "one activation refusing to launch must not 404 the delivery — providers read that as 'hook gone' and GitLab disables it.");
        gitlabStatus.ShouldBe(HttpStatusCode.OK, customMessage: "another team's delivery must be untouched by a refusing activation anywhere.");

        (await CountRunsAsync(githubHealthy)).ShouldBe(1, customMessage: "the sibling activation's run was rolled back with the refusal — check the per-activation catch in RunSourceDispatcher.");
        (await CountRunsAsync(gitlabHealthy)).ShouldBe(1);
        (await CountRunsAsync(siblingPoison)).ShouldBe(0);
        (await CountRunsAsync(foreignPoison)).ShouldBe(0);

        var refusal = await LoadRejectionAsync(github.TeamId, deliveryId);
        refusal.ShouldNotBeNull(customMessage: "the refusing activation must leave a Rejected row naming why, as the authority-denied branch does.");
        refusal.ShouldContain(WorkflowRunRequestRejectionReasons.CompletionAdmissionRefused);
        refusal.ShouldContain(siblingPoison.ToString());
    }

    [Fact]
    public async Task A_connection_delivery_naming_a_repository_outside_the_hooks_owner_path_starts_nothing()
    {
        var connection = await SeedConnectionWithTwoOwnersAsync();
        var mergedConfig = JsonSerializer.Serialize(new { repositories = new[] { new { repositoryId = connection.OtherRepositoryId, labels = new[] { "release" } } } });
        var workflow = await CreateWorkflowAsync(connection.UserId, connection.TeamId, WorkflowBody("trigger.pr.merged", mergedConfig));
        var body = GitLabMergedBody(connection.OtherExternalId, connection.OtherFullPath);

        var crossOwner = await PostGitLabAsync($"/api/webhooks/connection/{connection.AcmeHookId}", body, connection.AcmeToken);

        crossOwner.ShouldBe(HttpStatusCode.OK, customMessage: "an out-of-scope repository is ordinary group traffic — 200 and an audit row, never a retryable failure.");
        (await CountRunsAsync(workflow)).ShouldBe(0, customMessage: "the 'acme' hook's token forged a merge for 'secretgroup/payments'. Check RouteConnectionDeliveryAsync applies OwnerPathHierarchy.Covers(hook.OwnerPath, repository.NamespacePath).");

        var ownOwner = await PostGitLabAsync($"/api/webhooks/connection/{connection.OtherHookId}", body, connection.OtherToken);

        ownOwner.ShouldBe(HttpStatusCode.OK);
        (await CountRunsAsync(workflow)).ShouldBe(1, customMessage: "the hook that DOES cover the repository must still route it.");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_retired_hook_starts_nothing(bool removeRepositoryThroughTheApi)
    {
        // A hook the operator finished by hand from the Webhook tab's setup steps: CodeSpace never learned its
        // remote id, so an unbind cannot delete it at the provider and the row is CAS'd to Cancelled instead.
        var seed = await SeedTeamWithRepositoryHookAsync(ProviderKind.GitHub, "retired/repo", RepositoryWebhookRegistrationStatus.DeadLettered, hookExternalId: null);
        var workflow = await CreateWorkflowAsync(seed.UserId, seed.TeamId, WorkflowBody("trigger.pr.opened", ScopedTo(seed.RepositoryId)));

        if (removeRepositoryThroughTheApi) await RemoveRepositoryAsync(seed);
        else await CancelHookAsync(seed.WebhookId);

        var status = await PostGitHubAsync(seed, "pull_request", GitHubPrBody("opened", "after-retire"));

        status.ShouldBe(HttpStatusCode.NotFound, customMessage: "a retired hook is refused before its body is read, exactly as a retired connection hook is.");
        (await CountRunsAsync(workflow)).ShouldBe(0, customMessage: "a delivery on a Cancelled hook / for a removed repository started a run. Check WebhookIngestionService.IngestAsync gates on WebhookRegistrationLifecycle and Repository.DeletedDate.");
    }

    [Theory]
    [InlineData(ProviderWebhookScope.Repository)]
    [InlineData(ProviderWebhookScope.Connection)]
    public async Task Two_teams_binding_the_same_provider_repository_each_receive_only_their_own_deliveries(ProviderWebhookScope scope)
    {
        // Both teams bind the SAME remote repository on the same host. Routing by the provider's identity instead of by
        // the hook's own repository row is exactly the shortcut that would hand one tenant's delivery to the other.
        var projectId = NextGitLabProjectId();
        var first = await SeedTeamBindingSharedRepositoryAsync(scope, projectId);
        var second = await SeedTeamBindingSharedRepositoryAsync(scope, projectId);
        var firstWorkflow = await CreateWorkflowAsync(first.UserId, first.TeamId, WorkflowBody("trigger.pr.opened", "{}"));
        var secondWorkflow = await CreateWorkflowAsync(second.UserId, second.TeamId, WorkflowBody("trigger.pr.opened", "{}"));
        var body = GitLabOpenedBody(projectId, "acme/web");

        (await PostGitLabAsync(first.HookUrl, body, first.Secret)).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(firstWorkflow)).ShouldBe(1);
        (await CountRunsAsync(secondWorkflow)).ShouldBe(0, customMessage: "the first team's delivery started a run in the second team because both bound the same provider repository. Routing must follow the hook's own repository row, never the provider id.");

        (await PostGitLabAsync(second.HookUrl, body, second.Secret)).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(firstWorkflow)).ShouldBe(1, customMessage: "the second team's delivery reached the first team.");
        (await CountRunsAsync(secondWorkflow)).ShouldBe(1);
    }

    [Fact]
    public async Task An_owner_rename_followed_by_a_delivery_still_starts_exactly_one_run()
    {
        // A group hook is keyed at the provider by the group's id, so it keeps delivering after a rename while its row keeps
        // the path it was registered under. Opening the repository re-syncs its NamespacePath, and from then on that hook
        // no longer covers it. The re-sync must restore coverage itself, or every later event is lost and nothing re-provisions.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (owner, renamed, projectId) = ($"acme-{suffix}", $"acme-corp-{suffix}", NextGitLabProjectId());
        using var provider = new StubProviderHost().Answer("GET", $"/api/v4/projects/{projectId}", 200, GitLabProjectJson(projectId, renamed));
        var seed = await SeedGitLabConnectionAsync(provider.BaseUrl, projectId, owner, new[] { owner });
        var oldHook = seed.Hooks[owner];
        var workflow = await CreateWorkflowAsync(seed.UserId, seed.TeamId, WorkflowBody("trigger.pr.opened", ScopedTo(seed.RepositoryId)));

        (await PostGitLabAsync(oldHook.Url, GitLabOpenedBody(projectId, $"{owner}/web"), oldHook.Token)).ShouldBe(HttpStatusCode.OK);
        (await CountRunsAsync(workflow)).ShouldBe(1, customMessage: "control: before anything re-syncs, the stored paths still agree and the hook routes.");

        (await RefreshRepositoryAsync(seed)).ShouldBe(renamed, customMessage: $"the refresh must read the renamed owner from the provider. It sent: {string.Join(", ", provider.Requests.Select(r => $"{r.Method} {r.PathAndQuery}"))}");

        var newHook = await LoadConnectionHookAsync(seed.InstanceId, renamed);
        newHook.ShouldNotBeNull(customMessage: "the refresh moved the repository out from under every hook and staged none. Check RepositoryService.RefreshMetadataBestEffortAsync stages coverage through IConnectionWebhookCoverageService.");

        // From here the provider sends each event on both of the renamed group's hooks.
        var afterRename = GitLabOpenedBody(projectId, $"{renamed}/web");
        (await PostGitLabAsync(oldHook.Url, afterRename, oldHook.Token)).ShouldBe(HttpStatusCode.OK);
        (await PostGitLabAsync(newHook!.Url, afterRename, newHook.Token)).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(workflow)).ShouldBe(2, customMessage: "the event delivered after the rename must start exactly one run: the new hook routes it, the stale one is refused.");
        (await LoadRepositoryRefusalsAsync(seed.TeamId, seed.RepositoryId)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.RepositoryOutsideHookOwner), customMessage: "the stale hook's copy must be refused on the repository's own tab, under a reason that says why.");
    }

    [Fact]
    public async Task A_repository_transferred_into_another_hooked_owner_routes_once_its_placement_is_refreshed()
    {
        // The other direction of the same staleness: the project moved between two owners this connection both hooks. The
        // new owner's hook delivers while the stored path still names the old owner, so until the repository is re-synced
        // the delivery is refused — on the repository's own tab, saying why — and the re-sync alone restores routing, with
        // no new hook, because the new owner was already covered.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (from, to, projectId) = ($"from-{suffix}", $"to-{suffix}", NextGitLabProjectId());
        using var provider = new StubProviderHost().Answer("GET", $"/api/v4/projects/{projectId}", 200, GitLabProjectJson(projectId, to));
        var seed = await SeedGitLabConnectionAsync(provider.BaseUrl, projectId, from, new[] { from, to });
        var hook = seed.Hooks[to];
        var workflow = await CreateWorkflowAsync(seed.UserId, seed.TeamId, WorkflowBody("trigger.pr.opened", ScopedTo(seed.RepositoryId)));
        var body = GitLabOpenedBody(projectId, $"{to}/web");

        (await PostGitLabAsync(hook.Url, body, hook.Token)).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(workflow)).ShouldBe(0, customMessage: "before the re-sync the stored path names the old owner, which this hook does not cover.");
        (await LoadRepositoryRefusalsAsync(seed.TeamId, seed.RepositoryId)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.RepositoryOutsideHookOwner), customMessage: "the refusal must land on the repository's own tab, not read as 'not bound'.");

        (await RefreshRepositoryAsync(seed)).ShouldBe(to);
        (await CountConnectionHooksAsync(seed.InstanceId)).ShouldBe(2, customMessage: "the new owner was already covered; the re-sync must not stage a second hook on it.");

        (await PostGitLabAsync(hook.Url, body, hook.Token)).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(workflow)).ShouldBe(1, customMessage: "once re-synced, the new owner's hook covers the repository and routes it.");
    }

    // ─── Requests ──────────────────────────────────────────────────────────────

    private static HttpRequestMessage Authed(HttpMethod method, string url, Guid userId, Guid teamId, string? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>The frontend's own shape: a trigger node with an empty config and one activation for it — <c>deriveActivations</c> emits <c>config ?? {}</c>.</summary>
    private static string WorkflowBody(string typeKey, string activationConfigJson, string? completionMode = null)
    {
        var mode = completionMode == null ? "" : $",\"completionMode\":\"{completionMode}\"";

        return "{\"name\":\"tenancy-" + Guid.NewGuid().ToString("N")[..6] + "\",\"enabled\":true,\"definition\":{\"schemaVersion\":1" + mode
            + ",\"nodes\":[{\"id\":\"trigger\",\"typeKey\":\"" + typeKey + "\",\"label\":\"t\",\"config\":{},\"inputs\":{}},{\"id\":\"end\",\"typeKey\":\"builtin.terminal\",\"label\":\"Done\",\"config\":{},\"inputs\":{}}]"
            + ",\"edges\":[{\"from\":\"trigger\",\"to\":\"end\"}]},\"activations\":[{\"typeKey\":\"" + typeKey + "\",\"enabled\":true,\"config\":" + activationConfigJson + "}]}";
    }

    private static string ScopedTo(Guid repositoryId) => JsonSerializer.Serialize(new { repositories = new[] { new { repositoryId } } });

    private async Task<(HttpStatusCode Status, string Body)> SendWorkflowAsync(Guid userId, Guid teamId, string json)
    {
        var response = await _factory.CreateClient().SendAsync(Authed(HttpMethod.Post, "/api/workflows", userId, teamId, json));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<Guid> CreateWorkflowAsync(Guid userId, Guid teamId, string json)
    {
        var (status, body) = await SendWorkflowAsync(userId, teamId, json);
        status.ShouldBe(HttpStatusCode.OK, customMessage: $"seeding a workflow over POST /api/workflows failed: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<HttpStatusCode> PostGitHubAsync(TeamRepository seed, string eventName, string body, string? deliveryId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{seed.WebhookId}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Event", eventName);
        request.Headers.Add("X-GitHub-Delivery", deliveryId ?? $"d-{Guid.NewGuid():N}");
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(seed.Secret));
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        return (await _factory.CreateClient().SendAsync(request)).StatusCode;
    }

    private Task<HttpStatusCode> PostGitLabAsync(TeamRepository seed, string body) => PostGitLabAsync($"/api/webhooks/{seed.WebhookId}", body, seed.Secret);

    private async Task<HttpStatusCode> PostGitLabAsync(string url, string body, string token)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Gitlab-Event", "Merge Request Hook");
        request.Headers.Add("X-Gitlab-Event-UUID", $"u-{Guid.NewGuid():N}");
        request.Headers.Add("X-Gitlab-Token", token);
        return (await _factory.CreateClient().SendAsync(request)).StatusCode;
    }

    /// <summary>The repository page's background read: <c>GET /api/repositories/{id}?refresh=true</c>, which re-syncs placement from the provider. Returns the NamespacePath it answers with.</summary>
    private async Task<string?> RefreshRepositoryAsync(GitLabConnection seed)
    {
        var response = await _factory.CreateClient().SendAsync(Authed(HttpMethod.Get, $"/api/repositories/{seed.RepositoryId}?refresh=true", seed.UserId, seed.TeamId));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: body);
        return JsonDocument.Parse(body).RootElement.GetProperty("namespacePath").GetString();
    }

    private async Task RemoveRepositoryAsync(TeamRepository seed)
    {
        var response = await _factory.CreateClient().SendAsync(Authed(HttpMethod.Delete, $"/api/repositories/{seed.RepositoryId}", seed.UserId, seed.TeamId));
        response.StatusCode.ShouldBe(HttpStatusCode.NoContent, customMessage: $"the real unbind must succeed for this scenario: {await response.Content.ReadAsStringAsync()}");
    }

    // ─── Bodies ────────────────────────────────────────────────────────────────

    private static string GitHubPrBody(string action, string marker) =>
        "{\"action\":\"" + action + "\",\"pull_request\":{\"id\":101,\"number\":77,\"title\":\"" + marker + "-title\",\"body\":\"" + marker + "-body\",\"head\":{\"ref\":\"" + marker + "-branch\",\"sha\":\"s\"},\"base\":{\"ref\":\"main\"},\"user\":{\"id\":9,\"login\":\"team-dev\"},\"html_url\":\"https://github.example/pull/77\",\"labels\":[{\"name\":\"confidential\"}]}}";

    private static string GitHubPushBody(string marker) =>
        "{\"ref\":\"refs/heads/main\",\"before\":\"aaaa\",\"after\":\"bbbb\",\"commits\":[{\"id\":\"bbbb\",\"message\":\"" + marker + "\",\"author\":{\"email\":\"d@a\",\"name\":\"d\"}}],\"pusher\":{\"name\":\"" + marker + "-pusher\"},\"sender\":{\"id\":3,\"login\":\"pusher\"}}";

    private static string GitLabMergeRequestBody() =>
        "{\"object_kind\":\"merge_request\",\"user\":{\"id\":1,\"username\":\"u\"},\"project\":{\"id\":1},\"object_attributes\":{\"id\":1,\"iid\":3,\"action\":\"open\",\"title\":\"victim\",\"description\":\"d\",\"source_branch\":\"f\",\"target_branch\":\"main\",\"url\":\"https://x\"},\"labels\":[]}";

    private static string GitLabOpenedBody(string projectId, string path) =>
        "{\"object_kind\":\"merge_request\",\"user\":{\"id\":1,\"username\":\"u\"},\"project\":{\"id\":" + projectId + ",\"path_with_namespace\":\"" + path + "\"},\"object_attributes\":{\"id\":1,\"iid\":3,\"action\":\"open\",\"title\":\"t\",\"description\":\"d\",\"source_branch\":\"f\",\"target_branch\":\"main\",\"url\":\"https://x\"},\"labels\":[]}";

    /// <summary>What GitLab answers for <c>GET /api/v4/projects/:id</c> — trimmed to the fields the provider class maps.</summary>
    private static string GitLabProjectJson(string projectId, string owner) =>
        "{\"id\":" + projectId + ",\"name\":\"web\",\"path\":\"web\",\"path_with_namespace\":\"" + owner + "/web\",\"namespace\":{\"id\":7,\"name\":\"" + owner + "\",\"path\":\"" + owner + "\",\"kind\":\"group\",\"full_path\":\"" + owner + "\"},\"default_branch\":\"main\",\"visibility\":\"private\",\"web_url\":\"https://gl.invalid/" + owner + "/web\",\"archived\":false}";

    /// <summary>A GitLab project id: numeric, because the identifier reads <c>project.id</c> as a number. Unique per call so seeds on a shared host never collide.</summary>
    private static string NextGitLabProjectId() => Random.Shared.Next(100_000, 999_999).ToString();

    private static string GitLabMergedBody(string projectId, string path) =>
        "{\"object_kind\":\"merge_request\",\"user\":{\"id\":1,\"username\":\"acme-owner\"},\"project\":{\"id\":" + projectId + ",\"path_with_namespace\":\"" + path + "\"},\"object_attributes\":{\"id\":1,\"iid\":3,\"action\":\"merge\",\"merge_commit_sha\":\"deadbeef\"},\"labels\":[{\"title\":\"release\"}]}";

    // ─── Seeds ─────────────────────────────────────────────────────────────────

    private sealed record TeamRepository(Guid UserId, Guid TeamId, Guid RepositoryId, Guid WebhookId, string Secret);

    private sealed record PersonalUser(Guid UserId, Guid TeamId);

    private sealed record SharedRepositoryBinding(Guid UserId, Guid TeamId, Guid RepositoryId, string HookUrl, string Secret);

    private sealed record ConnectionHook(Guid Id, string Token)
    {
        public string Url => $"/api/webhooks/connection/{Id}";
    }

    private sealed record GitLabConnection(Guid UserId, Guid TeamId, Guid InstanceId, Guid RepositoryId, IReadOnlyDictionary<string, ConnectionHook> Hooks);

    private sealed record TwoOwnerConnection(Guid UserId, Guid TeamId, Guid AcmeHookId, string AcmeToken, Guid OtherHookId, string OtherToken, Guid OtherRepositoryId, string OtherExternalId, string OtherFullPath);

    private async Task<TeamRepository> SeedTeamWithRepositoryHookAsync(ProviderKind provider, string fullPath, RepositoryWebhookRegistrationStatus status = RepositoryWebhookRegistrationStatus.Registered, string? hookExternalId = "wh-1")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var seed = new TeamRepository(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"sec-{Guid.NewGuid():N}");
        var instanceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();
        var slash = fullPath.IndexOf('/');

        db.User.Add(new User { Id = seed.UserId, SecurityStamp = TestToken.SeedStamp, Email = $"owner-{suffix}@test.local", Name = "Owner", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = seed.TeamId, Slug = $"team-{suffix}", Name = "Team", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = seed.TeamId, UserId = seed.UserId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = seed.TeamId, Provider = provider, DisplayName = "P", BaseUrl = $"https://p-{suffix}.invalid", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Credential.Add(new Credential { Id = credentialId, TeamId = seed.TeamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "PAT", EncryptedPayload = encryptor.Encrypt("{\"token\":\"x\"}"), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = seed.RepositoryId, TeamId = seed.TeamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = $"ext-{suffix}", NamespacePath = fullPath[..slash], Name = fullPath[(slash + 1)..], FullPath = $"{fullPath}-{suffix}", Visibility = RepositoryVisibility.Private, WebUrl = $"https://p.invalid/{fullPath}", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.RepositoryWebhook.Add(new RepositoryWebhook { Id = seed.WebhookId, RepositoryId = seed.RepositoryId, ExternalId = hookExternalId, CallbackUrl = $"https://x/cb/{suffix}", SecretEnc = encryptor.Encrypt(seed.Secret), Active = true, SubscribedEvents = new List<string> { "*" }, RegistrationStatus = status, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return seed;
    }

    /// <summary>A brand-new user whose ONLY team is the Personal team every non-system user owns. No instance role, no repositories.</summary>
    private async Task<PersonalUser> SeedPersonalOnlyUserAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var user = new PersonalUser(Guid.NewGuid(), Guid.NewGuid());

        db.User.Add(new User { Id = user.UserId, SecurityStamp = TestToken.SeedStamp, Email = $"outsider-{suffix}@test.local", Name = "Outsider", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = user.TeamId, Slug = $"personal-{suffix}", Name = "Personal", Kind = TeamKind.Personal, PersonalForUserId = user.UserId, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = user.TeamId, UserId = user.UserId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>One GitLab connection, two Registered group hooks with independent tokens on owners <c>acme</c> and <c>secretgroup</c>, one bound repository under each.</summary>
    private async Task<TwoOwnerConnection> SeedConnectionWithTwoOwnersAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var otherExternalId = (DateTime.UtcNow.Ticks % 1000000 + 5000).ToString();
        var seed = new TwoOwnerConnection(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"tok-acme-{Guid.NewGuid():N}", Guid.NewGuid(), $"tok-sg-{Guid.NewGuid():N}", Guid.NewGuid(), otherExternalId, $"secretgroup/payments-{suffix}");
        var instanceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        db.User.Add(new User { Id = seed.UserId, SecurityStamp = TestToken.SeedStamp, Email = $"conn-{suffix}@test.local", Name = "Conn", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = seed.TeamId, Slug = $"conn-{suffix}", Name = "Conn", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = seed.TeamId, UserId = seed.UserId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = seed.TeamId, Provider = ProviderKind.GitLab, DisplayName = "GL", BaseUrl = $"https://gl-{suffix}.invalid", WebhookScope = ProviderWebhookScope.Connection, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Credential.Add(new Credential { Id = credentialId, TeamId = seed.TeamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "PAT", EncryptedPayload = encryptor.Encrypt("{}"), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = Guid.NewGuid(), TeamId = seed.TeamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = $"1{otherExternalId}", NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{suffix}", WebUrl = "https://gl/acme/api", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = seed.OtherRepositoryId, TeamId = seed.TeamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = otherExternalId, NamespacePath = "secretgroup", Name = $"payments-{suffix}", FullPath = seed.OtherFullPath, WebUrl = "https://gl/secretgroup/payments", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ConnectionWebhook.Add(new ConnectionWebhook { Id = seed.AcmeHookId, ProviderInstanceId = instanceId, CredentialId = credentialId, OwnerPath = "acme", ExternalId = "g1", CallbackUrl = "https://x/a", SecretEnc = encryptor.Encrypt(seed.AcmeToken), SubscribedEvents = new List<string> { "merge_requests_events" }, Active = true, RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ConnectionWebhook.Add(new ConnectionWebhook { Id = seed.OtherHookId, ProviderInstanceId = instanceId, CredentialId = credentialId, OwnerPath = "secretgroup", ExternalId = "g2", CallbackUrl = "https://x/b", SecretEnc = encryptor.Encrypt(seed.OtherToken), SubscribedEvents = new List<string> { "merge_requests_events" }, Active = true, RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return seed;
    }

    /// <summary>
    /// One team binding provider repository <paramref name="projectId"/> as <c>acme/web</c> on a host every such team
    /// shares — through its own per-repository hook, or a group hook on <c>acme</c>, by <paramref name="scope"/>.
    /// </summary>
    private async Task<SharedRepositoryBinding> SeedTeamBindingSharedRepositoryAsync(ProviderWebhookScope scope, string projectId)
    {
        using var lifetime = _factory.Services.CreateScope();
        var db = lifetime.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = lifetime.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (userId, teamId, repositoryId, hookId, instanceId, credentialId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var secret = $"sec-{Guid.NewGuid():N}";

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"shared-{suffix}@test.local", Name = "Shared", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"shared-{suffix}", Name = "Shared", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "GL", BaseUrl = "https://shared-host.invalid", WebhookScope = scope, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Credential.Add(new Credential { Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "PAT", EncryptedPayload = encryptor.Encrypt("{}"), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repositoryId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = projectId, NamespacePath = "acme", Name = "web", FullPath = "acme/web", Visibility = RepositoryVisibility.Private, WebUrl = "https://shared-host.invalid/acme/web", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        if (scope == ProviderWebhookScope.Repository)
            db.RepositoryWebhook.Add(new RepositoryWebhook { Id = hookId, RepositoryId = repositoryId, ExternalId = "wh", CallbackUrl = $"https://x/cb/{suffix}", SecretEnc = encryptor.Encrypt(secret), Active = true, SubscribedEvents = new List<string> { "*" }, RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        else
            db.ConnectionWebhook.Add(new ConnectionWebhook { Id = hookId, ProviderInstanceId = instanceId, CredentialId = credentialId, OwnerPath = "acme", ExternalId = "g", CallbackUrl = $"https://x/c/{suffix}", SecretEnc = encryptor.Encrypt(secret), SubscribedEvents = new List<string> { "merge_requests_events" }, Active = true, RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();

        var hookUrl = scope == ProviderWebhookScope.Repository ? $"/api/webhooks/{hookId}" : $"/api/webhooks/connection/{hookId}";
        return new SharedRepositoryBinding(userId, teamId, repositoryId, hookUrl, secret);
    }

    /// <summary>
    /// A GitLab connection whose API is <paramref name="apiBaseUrl"/>, one bound repository <c>web</c> stored under
    /// <paramref name="repositoryOwner"/>, and a Registered group hook with its own token on each of <paramref name="hookOwners"/>.
    /// The credential is a real PAT payload, because the refresh reads the provider through it.
    /// </summary>
    private async Task<GitLabConnection> SeedGitLabConnectionAsync(string apiBaseUrl, string projectId, string repositoryOwner, IReadOnlyList<string> hookOwners)
    {
        using var lifetime = _factory.Services.CreateScope();
        var db = lifetime.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = lifetime.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var serializer = lifetime.ServiceProvider.GetRequiredService<ICredentialPayloadSerializer>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var (userId, teamId, instanceId, credentialId, repositoryId) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var hooks = hookOwners.ToDictionary(o => o, _ => new ConnectionHook(Guid.NewGuid(), $"tok-{Guid.NewGuid():N}"));

        db.User.Add(new User { Id = userId, SecurityStamp = TestToken.SeedStamp, Email = $"move-{suffix}@test.local", Name = "Move", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = teamId, Slug = $"move-{suffix}", Name = "Move", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = teamId, UserId = userId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = teamId, Provider = ProviderKind.GitLab, DisplayName = "GL", BaseUrl = apiBaseUrl, WebhookScope = ProviderWebhookScope.Connection, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Credential.Add(new Credential { Id = credentialId, TeamId = teamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "PAT", EncryptedPayload = encryptor.Encrypt(serializer.Serialize(new PatPayload { Token = "glpat-test" })), Status = CredentialStatus.Active, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = repositoryId, TeamId = teamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = projectId, NamespacePath = repositoryOwner, Name = "web", FullPath = $"{repositoryOwner}/web", Visibility = RepositoryVisibility.Private, WebUrl = $"{apiBaseUrl}/{repositoryOwner}/web", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        foreach (var (ownerPath, hook) in hooks)
            db.ConnectionWebhook.Add(new ConnectionWebhook { Id = hook.Id, ProviderInstanceId = instanceId, CredentialId = credentialId, OwnerPath = ownerPath, ExternalId = $"g-{ownerPath}", CallbackUrl = $"https://x/c/{hook.Id}", SecretEnc = encryptor.Encrypt(hook.Token), SubscribedEvents = new List<string> { "merge_requests_events" }, Active = true, RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return new GitLabConnection(userId, teamId, instanceId, repositoryId, hooks);
    }

    /// <summary>
    /// A version stored before the save-time gate refused <c>completionMode: enforced</c> on a graph whose mode is
    /// not Enforceable — appended directly, because no API path can produce it any more. Versions are insert-only
    /// (the immutability trigger forbids UPDATE), so this is the same shape a pre-gate save left behind.
    /// </summary>
    private async Task AppendLegacyEnforcedVersionAsync(Guid workflowId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var workflow = await db.Workflow.SingleAsync(w => w.Id == workflowId);
        var current = await db.WorkflowVersion.AsNoTracking().SingleAsync(v => v.WorkflowId == workflowId && v.Version == workflow.LatestVersion);
        var definition = JsonSerializer.Deserialize<WorkflowDefinition>(current.DefinitionJson, WorkflowJson.Options)! with { CompletionMode = WorkflowDefinition.CompletionModeEnforced };
        var enforced = JsonSerializer.Serialize(definition, WorkflowJson.Options);

        workflow.LatestVersion += 1;
        workflow.DefinitionJson = enforced;
        db.WorkflowVersion.Add(new WorkflowVersion { WorkflowId = workflowId, Version = workflow.LatestVersion, DefinitionJson = enforced, DefinitionHash = DefinitionHash.Compute(definition), CommittedAt = DateTimeOffset.UtcNow, CreatedDate = DateTimeOffset.UtcNow, CreatedBy = workflow.CreatedBy });

        await db.SaveChangesAsync();
    }

    private async Task CancelHookAsync(Guid webhookId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().RepositoryWebhook.Where(w => w.Id == webhookId).ExecuteUpdateAsync(s => s.SetProperty(w => w.RegistrationStatus, RepositoryWebhookRegistrationStatus.Cancelled));
    }

    // ─── Reads ─────────────────────────────────────────────────────────────────

    private async Task<int> CountRunsAsync(Guid workflowId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.WorkflowId == workflowId);
    }

    private async Task<List<string>> LoadRequestPayloadsAsync(Guid teamId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking().Where(r => r.TeamId == teamId).Select(r => r.NormalizedPayloadJson).ToListAsync();
    }

    /// <summary>A hook the connection has on <paramref name="ownerPath"/>, with its token decrypted so a test can deliver as the provider would.</summary>
    private async Task<ConnectionHook?> LoadConnectionHookAsync(Guid instanceId, string ownerPath)
    {
        using var scope = _factory.Services.CreateScope();
        var row = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().SingleOrDefaultAsync(w => w.ProviderInstanceId == instanceId && w.OwnerPath == ownerPath);
        return row == null ? null : new ConnectionHook(row.Id, scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>().Decrypt(row.SecretEnc));
    }

    private async Task<int> CountConnectionHooksAsync(Guid instanceId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().ConnectionWebhook.AsNoTracking().CountAsync(w => w.ProviderInstanceId == instanceId);
    }

    private async Task<List<string>> LoadRepositoryRefusalsAsync(Guid teamId, Guid repositoryId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.RepositoryId == repositoryId && r.Status == WorkflowRunRequestStatus.Rejected)
            .Select(r => r.Error!)
            .ToListAsync();
    }

    private async Task<string?> LoadRejectionAsync(Guid teamId, string deliveryId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .Where(r => r.TeamId == teamId && r.ExternalEventId == deliveryId && r.Status == WorkflowRunRequestStatus.Rejected)
            .Select(r => r.Error)
            .SingleOrDefaultAsync();
    }
}

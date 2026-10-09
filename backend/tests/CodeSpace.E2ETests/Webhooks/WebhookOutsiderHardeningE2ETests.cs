using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CodeSpace.Api.Controllers;
using CodeSpace.Api.Extensions;
using CodeSpace.Core.Persistence.Db;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Credentials;
using CodeSpace.Core.Services.Workflows.Engine;
using CodeSpace.E2ETests.Infrastructure;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Constants;
using CodeSpace.Messages.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace CodeSpace.E2ETests.Webhooks;

/// <summary>
/// What an outsider can make the webhook endpoints do, on the WIRE. Each case is a request a stranger can send — a fork PR
/// on a public repository, a close/reopen loop, a captured signed body posted again, a forged signature, an oversized or
/// unaddressed body, a flood — and the status, runs and refusal rows it must leave behind.
///
/// <para>Tier: 🟢 High-fidelity — real app host (<see cref="WebhookApiFactory"/>), real routing, rate limiter, controller,
/// MediatR transaction, signature verifiers, normalizers, <c>RunSourceDispatcher</c> and <c>RunStarter</c>, against real
/// Postgres. The GitLab case answers the real GitLab provider class over the wire from a loopback
/// <see cref="StubProviderHost"/>. Only the Hangfire client is a no-op, so a started run is a row and never executes.</para>
/// </summary>
[Trait("Category", "E2E")]
[Trait("Surface", "Http")]
public sealed class WebhookOutsiderHardeningE2ETests : IClassFixture<WebhookApiFactory>
{
    private readonly WebhookApiFactory _factory;

    public WebhookOutsiderHardeningE2ETests(WebhookApiFactory factory) { _factory = factory; }

    // ─── Who may start a run ───────────────────────────────────────────────────

    [Fact]
    public async Task An_outsiders_fork_pr_on_a_public_repository_starts_nothing_by_default_and_says_why()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Public);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", "{}");

        var status = await PostGitHubAsync(seed, "pull_request", GitHubPr("reopened", number: 7, association: "NONE", headRepo: Fork, visibility: "public"));

        status.ShouldBe(HttpStatusCode.OK);
        (await CountRunsAsync(workflow)).ShouldBe(0, customMessage: "An activation saved without an authors choice must admit only members on a public repository. Check PullRequestTriggerAuthors.Required and the admission call in RunSourceDispatcher.FireIfMatchesAsync.");
        (await LoadRefusalsAsync(seed)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.AuthorNotMember), customMessage: "the operator must be told why the PR started nothing, not just see silence");
    }

    [Fact]
    public async Task A_members_pr_on_a_public_repository_runs_and_its_payload_says_who_wrote_it()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Public);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", "{}");

        await PostGitHubAsync(seed, "pull_request", GitHubPr("opened", number: 8, association: "MEMBER", headRepo: SameRepository, visibility: "public"));

        var payload = await LoadOnlyRunPayloadAsync(workflow);
        payload.GetProperty("authorAssociation").GetString().ShouldBe("member");
        payload.GetProperty("isFork").GetBoolean().ShouldBeFalse();
        payload.GetProperty("headRepositoryFullName").GetString().ShouldBe("acme/api");
    }

    [Fact]
    public async Task An_activation_that_admits_any_author_runs_an_outsiders_fork_pr_and_can_see_it_is_one()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Public);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", """{"authors":"any"}""");

        await PostGitHubAsync(seed, "pull_request", GitHubPr("opened", number: 9, association: "FIRST_TIME_CONTRIBUTOR", headRepo: Fork, visibility: "public"));

        var payload = await LoadOnlyRunPayloadAsync(workflow);
        payload.GetProperty("authorAssociation").GetString().ShouldBe("contributor");
        payload.GetProperty("isFork").GetBoolean().ShouldBeTrue();
        payload.GetProperty("headRepositoryFullName").GetString().ShouldBe("evil/repo");
        payload.GetProperty("sourceBranch").GetString().ShouldBe("main", "the fork's head branch is named like the base branch — which is exactly why isFork has to be on the payload");
    }

    [Fact]
    public async Task An_existing_activation_on_a_private_repository_still_admits_any_author()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", "{}");

        await PostGitHubAsync(seed, "pull_request", GitHubPr("opened", number: 10, association: "NONE", headRepo: Fork, visibility: "private"));

        (await CountRunsAsync(workflow)).ShouldBe(1, customMessage: "the default only narrows on PUBLIC repositories; a private repository's activations keep their behaviour");
    }

    [Fact]
    public async Task An_existing_activation_on_an_internal_repository_admits_only_members()
    {
        // Every member of the enterprise can read an internal repository, fork it and open a PR from the fork.
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Internal);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", "{}");

        await PostGitHubAsync(seed, "pull_request", GitHubPr("opened", number: 16, association: "NONE", headRepo: Fork, visibility: "internal"));
        await PostGitHubAsync(seed, "pull_request", GitHubPr("opened", number: 17, association: "MEMBER", headRepo: SameRepository, visibility: "internal"));

        (await CountRunsAsync(workflow)).ShouldBe(1, customMessage: "on an internal repository an activation saved without an authors choice admits the member's PR and not the outsider's — check PullRequestTriggerAuthors.Default");
        (await LoadRefusalsAsync(seed)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.AuthorNotMember));
    }

    [Fact]
    public async Task An_outsiders_push_to_a_members_pr_from_the_outsiders_fork_starts_nothing_on_a_public_repository()
    {
        // GitHub lets a member open a PR from anyone's fork branch; from then on the fork's owner pushes the code it runs.
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Public);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.updated", "{}");

        (await PostGitHubAsync(seed, "pull_request", GitHubSync(number: 18, after: "sha-evil", association: "MEMBER", headRepo: Fork, sender: SenderOutsider, visibility: "public"))).ShouldBe(HttpStatusCode.OK);
        (await PostGitHubAsync(seed, "pull_request", GitHubSync(number: 18, after: "sha-own", association: "MEMBER", headRepo: Fork, sender: SenderAuthor, visibility: "public"))).ShouldBe(HttpStatusCode.OK);

        (await LoadRunPayloadsAsync(workflow, "newHeadSha")).ShouldBe(new[] { "sha-own" }, ignoreOrder: true, customMessage: "the outsider's push must start nothing and the member author's own push must run — check the pusher rule in PullRequestTriggerAuthors.AdmitsPusher");
        (await LoadRefusalsAsync(seed)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.AuthorNotMember) && e.Contains("pushed by"));
    }

    [Fact]
    public async Task A_gitlab_fork_mr_on_a_public_project_runs_only_when_its_author_holds_developer()
    {
        using var gitlab = new StubProviderHost();
        var projectId = Random.Shared.Next(100_000, 999_999).ToString();
        gitlab.Answer("GET", $"/api/v4/projects/{projectId}/members/all/51", 200, """{"id":51,"username":"dev","access_level":30}""")
              .Answer("GET", $"/api/v4/projects/{projectId}/members/all/52", 404, """{"message":"404 Not found"}""");
        var seed = await SeedGitLabRepositoryAsync(gitlab.BaseUrl, projectId);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", "{}");

        (await PostGitLabAsync(seed, GitLabMr(projectId, iid: 1, authorId: 51))).ShouldBe(HttpStatusCode.OK);
        (await PostGitLabAsync(seed, GitLabMr(projectId, iid: 2, authorId: 52))).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(workflow)).ShouldBe(1, customMessage: "the Developer's MR must run and the non-member's must not — check GitLabRepositoryProvider.GetMemberStandingAsync and PullRequestTriggerAdmission");
        (await LoadOnlyRunPayloadAsync(workflow)).GetProperty("authorAssociation").GetString().ShouldBe("member");
        (await LoadRefusalsAsync(seed)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.AuthorNotMember));
        gitlab.Requests.ShouldContain(r => r.PathAndQuery.Contains("/members/all/52"), customMessage: "the standing must come from the provider, not be guessed from the payload");
    }

    // ─── How often ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Closing_and_reopening_a_pr_over_and_over_starts_one_run()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", """{"authors":"any"}""");

        for (var i = 0; i < 3; i++)
            (await PostGitHubAsync(seed, "pull_request", GitHubPr("reopened", number: 11, association: "NONE", headRepo: Fork, visibility: "private", updatedAt: i))).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(workflow)).ShouldBe(1, customMessage: "three reopens inside the debounce window are one run, not three — check PullRequestTriggerAdmission.EnsureNotDebouncedAsync");
        (await LoadRefusalsAsync(seed)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.PullRequestDebounced));
    }

    [Fact]
    public async Task A_members_second_push_inside_a_minute_starts_a_run_for_the_commit_it_pushed()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.updated", "{}");

        await PostGitHubAsync(seed, "pull_request", GitHubSync(number: 19, after: "sha-first", association: "MEMBER", headRepo: SameRepository, sender: SenderAuthor, visibility: "private"));
        await PostGitHubAsync(seed, "pull_request", GitHubSync(number: 19, after: "sha-amended", association: "MEMBER", headRepo: SameRepository, sender: SenderAuthor, visibility: "private"));

        (await LoadRunPayloadsAsync(workflow, "newHeadSha")).ShouldBe(new[] { "sha-first", "sha-amended" }, ignoreOrder: true, customMessage: "the commit that ends up on the PR must get a run — the debounce holds off the same head, never newer code");
    }

    [Fact]
    public async Task A_delivery_posted_again_after_the_window_does_not_hold_off_the_next_event()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", "{}");
        var opened = GitHubPr("opened", number: 20, association: "MEMBER", headRepo: SameRepository, visibility: "private");

        (await PostGitHubAsync(seed, "pull_request", opened, deliveryId: "orig-1")).ShouldBe(HttpStatusCode.OK);
        await ExpireDebounceAsync(seed);
        (await PostGitHubAsync(seed, "pull_request", opened, deliveryId: "orig-1")).ShouldBe(HttpStatusCode.OK);
        (await PostGitHubAsync(seed, "pull_request", GitHubPr("reopened", number: 20, association: "MEMBER", headRepo: SameRepository, visibility: "private"))).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(workflow)).ShouldBe(2, customMessage: "the same delivery posted again — Redeliver, or a captured body with its own id — already had its run and must not restart the window; check the already-started check in RunSourceDispatcher.FireIfMatchesAsync");
        (await LoadRefusalsAsync(seed)).ShouldNotContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.PullRequestDebounced));
    }

    [Fact]
    public async Task Different_pull_requests_are_not_debounced_against_each_other()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.opened", "{}");

        await PostGitHubAsync(seed, "pull_request", GitHubPr("opened", number: 12, association: "MEMBER", headRepo: SameRepository, visibility: "private"));
        await PostGitHubAsync(seed, "pull_request", GitHubPr("opened", number: 13, association: "MEMBER", headRepo: SameRepository, visibility: "private"));

        (await CountRunsAsync(workflow)).ShouldBe(2);
    }

    // ─── Replay ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_captured_body_posted_again_under_a_fresh_delivery_id_starts_nothing_and_a_redelivery_stays_one_run()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.merged", "{}");
        var captured = GitHubMerged(number: 14);

        (await PostGitHubAsync(seed, "pull_request", captured, deliveryId: "captured-1")).ShouldBe(HttpStatusCode.OK);
        (await PostGitHubAsync(seed, "pull_request", captured, deliveryId: "attacker-2")).ShouldBe(HttpStatusCode.OK);
        (await PostGitHubAsync(seed, "pull_request", captured, deliveryId: "captured-1")).ShouldBe(HttpStatusCode.OK);

        (await CountRunsAsync(workflow)).ShouldBe(1, customMessage: "an old merge replayed with a fresh X-GitHub-Delivery must not re-fire a release workflow — check WebhookIngestionService.ClaimBodyOrAuditReplayAsync");
        (await LoadRefusalsAsync(seed)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.DeliveryReplayed));
    }

    [Fact]
    public async Task A_signed_github_delivery_without_a_delivery_id_is_refused()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);
        var workflow = await CreateWorkflowAsync(seed, "trigger.pr.merged", "{}");
        var body = GitHubMerged(number: 15);

        var request = SignedGitHubRequest(seed, "pull_request", body, deliveryId: null);
        var response = await _factory.CreateClient().SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CountRunsAsync(workflow)).ShouldBe(0, customMessage: "without a delivery id every post of one body reads as a new delivery; GitHub always sends one");
        (await LoadRefusalsAsync(seed)).ShouldContain(e => e.StartsWith(WorkflowRunRequestRejectionReasons.DeliveryIdMissing));
    }

    // ─── Callbacks ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_callback_token_the_engine_minted_resumes_its_run_over_http()
    {
        // The callback route admits only the minted token's shape; this proves a token the engine really minted fits it.
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);
        var workflow = await CreateCallbackWorkflowAsync(seed);
        await PostGitHubAsync(seed, "pull_request", GitHubMerged(number: 21));
        var runId = await LoadOnlyRunIdAsync(workflow);

        await RunEngineAsync(runId);
        var token = await LoadCallbackTokenAsync(runId);

        var response = await _factory.CreateClient().PostAsync($"/api/workflows/callbacks/{token}", new StringContent("""{"status":"done"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: $"the engine minted '{token}' ({token.Length} chars) and the callback route did not take it — the {{token:length(...)}} constraint on WorkflowCallbacksController.Resume must match what WorkflowEngine mints");
        (await response.Content.ReadAsStringAsync()).ShouldContain("\"resumed\":true");
    }

    // ─── What an unauthenticated stranger costs ────────────────────────────────

    [Fact]
    public async Task Forged_signatures_leave_one_bounded_refusal_row_however_many_arrive()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);

        for (var i = 0; i < 20; i++)
        {
            var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{seed.WebhookId}") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            request.Headers.Add("X-GitHub-Event", "pull_request");
            request.Headers.Add("X-Hub-Signature-256", "sha256=" + new string('f', 64));
            request.Headers.TryAddWithoutValidation("User-Agent", "<img src=x onerror=alert(1)>" + new string('a', 3000));
            request.Headers.TryAddWithoutValidation("X-" + new string('j', 2000) + i, "junk");

            (await _factory.CreateClient().SendAsync(request)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var rows = await LoadRejectedRowsAsync(seed);
        rows.Count.ShouldBe(1, customMessage: "twenty forged posts must not push fifty genuine refusals out of the operator's panel — check WebhookIngestionService.BuildRefusalWindowKey on the signature refusal");
        rows[0].Error.ShouldStartWith(WorkflowRunRequestRejectionReasons.SignatureInvalid);
        rows[0].RawHeadersRedactedJson!.Length.ShouldBeLessThan(4096, "the stored headers are bounded whatever the stranger posted");
    }

    [Fact]
    public async Task A_body_posted_to_an_id_naming_no_hook_is_refused_before_it_is_read()
    {
        // Larger than the size limit: if the body were read before the hook was resolved, this would be a 413.
        var response = await PostRawAsync($"/api/webhooks/{Guid.NewGuid()}", WebhooksController.MaxDeliveryBodyBytes + 1);
        var connection = await PostRawAsync($"/api/webhooks/connection/{Guid.NewGuid()}", WebhooksController.MaxDeliveryBodyBytes + 1);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        connection.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task A_body_larger_than_any_provider_sends_is_refused()
    {
        var seed = await SeedGitHubRepositoryAsync(RepositoryVisibility.Private);

        var response = await PostRawAsync($"/api/webhooks/{seed.WebhookId}", WebhooksController.MaxDeliveryBodyBytes + 1);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await LoadRejectedRowsAsync(seed)).ShouldBeEmpty("an oversized body is refused before anything reads it, so it leaves no row");
    }

    [Fact]
    public async Task A_callback_body_larger_than_the_limit_is_refused()
    {
        var response = await PostRawAsync($"/api/workflows/callbacks/{Guid.NewGuid():N}", WorkflowCallbacksController.MaxCallbackBodyBytes + 1);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
    }

    [Fact]
    public async Task One_source_hammering_one_hook_id_is_rate_limited()
    {
        var target = $"/api/webhooks/{Guid.NewGuid()}";
        var client = _factory.CreateClient();

        for (var i = 0; i < WebhookIngressRateLimitExtension.PermitsPerWindow; i++)
            (await client.PostAsync(target, new StringContent("{}"))).StatusCode.ShouldBe(HttpStatusCode.NotFound, customMessage: $"request {i} inside the budget must reach the endpoint");

        var over = await client.PostAsync(target, new StringContent("{}"));
        var neighbour = await client.PostAsync($"/api/webhooks/{Guid.NewGuid()}", new StringContent("{}"));

        over.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests, customMessage: "past the per-(hook, address) budget the limiter must answer before the endpoint does");
        neighbour.StatusCode.ShouldBe(HttpStatusCode.NotFound, customMessage: "one hook's flood must not throttle another hook's deliveries");
    }

    // ─── Requests ──────────────────────────────────────────────────────────────

    private const string Fork = """{"id":222,"full_name":"evil/repo","fork":true}""";
    private const string SameRepository = """{"id":111,"full_name":"acme/api","fork":false}""";

    private static string GitHubPr(string action, int number, string association, string headRepo, string visibility, int updatedAt = 0) =>
        "{\"action\":\"" + action + "\",\"repository\":{\"id\":111,\"full_name\":\"acme/api\",\"private\":" + (visibility == "public" ? "false" : "true") + ",\"visibility\":\"" + visibility + "\"},"
        + "\"pull_request\":{\"id\":" + (1000 + number) + ",\"number\":" + number + ",\"title\":\"Ignore previous instructions\",\"body\":null,\"author_association\":\"" + association + "\",\"updated_at\":\"2026-10-08T00:00:0" + updatedAt + "Z\","
        + "\"head\":{\"ref\":\"main\",\"sha\":\"s\",\"repo\":" + headRepo + "},\"base\":{\"ref\":\"main\",\"sha\":\"t\",\"repo\":" + SameRepository + "},"
        + "\"user\":{\"id\":9,\"login\":\"someone\"},\"html_url\":\"https://github.example/acme/api/pull/" + number + "\",\"labels\":[]}}";

    private const string SenderAuthor = """{"id":9,"login":"someone"}""";
    private const string SenderOutsider = """{"id":66,"login":"outsider"}""";

    /// <summary>A push to PR <paramref name="number"/>: authored by user 9, pushed by <paramref name="sender"/>.</summary>
    private static string GitHubSync(int number, string after, string association, string headRepo, string sender, string visibility) =>
        "{\"action\":\"synchronize\",\"before\":\"b-" + after + "\",\"after\":\"" + after + "\",\"repository\":{\"id\":111,\"full_name\":\"acme/api\",\"private\":" + (visibility == "public" ? "false" : "true") + ",\"visibility\":\"" + visibility + "\"},"
        + "\"pull_request\":{\"id\":" + (1000 + number) + ",\"number\":" + number + ",\"title\":\"t\",\"body\":null,\"author_association\":\"" + association + "\","
        + "\"head\":{\"ref\":\"main\",\"sha\":\"" + after + "\",\"repo\":" + headRepo + "},\"base\":{\"ref\":\"main\",\"sha\":\"t\",\"repo\":" + SameRepository + "},"
        + "\"user\":{\"id\":9,\"login\":\"someone\"},\"html_url\":\"https://github.example/acme/api/pull/" + number + "\",\"labels\":[]},\"sender\":" + sender + "}";

    private static string GitHubMerged(int number) =>
        "{\"action\":\"closed\",\"repository\":{\"id\":111,\"full_name\":\"acme/api\",\"private\":true,\"visibility\":\"private\"},"
        + "\"pull_request\":{\"id\":" + (1000 + number) + ",\"number\":" + number + ",\"merged\":true,\"merge_commit_sha\":\"deadbeef\",\"labels\":[{\"name\":\"release\"}]},"
        + "\"sender\":{\"id\":3,\"login\":\"maintainer\"}}";

    private static string GitLabMr(string projectId, int iid, int authorId) =>
        "{\"object_kind\":\"merge_request\",\"user\":{\"id\":" + authorId + ",\"username\":\"u" + authorId + "\"},"
        + "\"project\":{\"id\":" + projectId + ",\"path_with_namespace\":\"acme/web\",\"visibility_level\":20},"
        + "\"object_attributes\":{\"id\":" + (9000 + iid) + ",\"iid\":" + iid + ",\"author_id\":" + authorId + ",\"source_project_id\":777,\"target_project_id\":" + projectId + ","
        + "\"source\":{\"path_with_namespace\":\"evil/web\"},\"target\":{\"path_with_namespace\":\"acme/web\"},"
        + "\"action\":\"open\",\"title\":\"t\",\"description\":\"d\",\"source_branch\":\"main\",\"target_branch\":\"main\",\"url\":\"https://x\"},\"labels\":[]}";

    private HttpRequestMessage SignedGitHubRequest(RepositorySeed seed, string eventName, string body, string? deliveryId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{seed.WebhookId}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-GitHub-Event", eventName);
        if (deliveryId != null) request.Headers.Add("X-GitHub-Delivery", deliveryId);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(seed.Secret));
        request.Headers.Add("X-Hub-Signature-256", "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant());
        return request;
    }

    private async Task<HttpStatusCode> PostGitHubAsync(RepositorySeed seed, string eventName, string body, string? deliveryId = null) =>
        (await _factory.CreateClient().SendAsync(SignedGitHubRequest(seed, eventName, body, deliveryId ?? $"d-{Guid.NewGuid():N}"))).StatusCode;

    private async Task<HttpStatusCode> PostGitLabAsync(RepositorySeed seed, string body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/webhooks/{seed.WebhookId}") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Gitlab-Event", "Merge Request Hook");
        request.Headers.Add("X-Gitlab-Event-UUID", $"u-{Guid.NewGuid():N}");
        request.Headers.Add("X-Gitlab-Token", seed.Secret);
        return (await _factory.CreateClient().SendAsync(request)).StatusCode;
    }

    private async Task<HttpResponseMessage> PostRawAsync(string url, long bytes)
    {
        var content = new ByteArrayContent(new byte[bytes]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        return await _factory.CreateClient().PostAsync(url, content);
    }

    private static HttpRequestMessage Authed(HttpMethod method, string url, Guid userId, Guid teamId, string? json = null)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TestToken.Mint(userId, TestToken.SeedStamp));
        request.Headers.Add("X-Team-Id", teamId.ToString());
        if (json != null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return request;
    }

    /// <summary>The frontend's own shape: a trigger node whose config IS the activation config, the way <c>deriveActivations</c> projects it.</summary>
    private async Task<Guid> CreateWorkflowAsync(RepositorySeed seed, string typeKey, string configJson)
    {
        var json = "{\"name\":\"outsider-" + Guid.NewGuid().ToString("N")[..6] + "\",\"enabled\":true,\"definition\":{\"schemaVersion\":1"
            + ",\"nodes\":[{\"id\":\"trigger\",\"typeKey\":\"" + typeKey + "\",\"label\":\"t\",\"config\":" + configJson + ",\"inputs\":{}},{\"id\":\"end\",\"typeKey\":\"builtin.terminal\",\"label\":\"Done\",\"config\":{},\"inputs\":{}}]"
            + ",\"edges\":[{\"from\":\"trigger\",\"to\":\"end\"}]},\"activations\":[{\"typeKey\":\"" + typeKey + "\",\"enabled\":true,\"config\":" + configJson + "}]}";

        var response = await _factory.CreateClient().SendAsync(Authed(HttpMethod.Post, "/api/workflows", seed.UserId, seed.TeamId, json));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: $"seeding a workflow over POST /api/workflows failed: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>A merged PR parks the run on <c>flow.wait_callback</c> until the tokened URL is posted to.</summary>
    private async Task<Guid> CreateCallbackWorkflowAsync(RepositorySeed seed)
    {
        var json = "{\"name\":\"callback-" + Guid.NewGuid().ToString("N")[..6] + "\",\"enabled\":true,\"definition\":{\"schemaVersion\":1"
            + ",\"nodes\":[{\"id\":\"trigger\",\"typeKey\":\"trigger.pr.merged\",\"label\":\"t\",\"config\":{},\"inputs\":{}},{\"id\":\"callback\",\"typeKey\":\"flow.wait_callback\",\"label\":\"Wait\",\"config\":{},\"inputs\":{}},{\"id\":\"end\",\"typeKey\":\"builtin.terminal\",\"label\":\"Done\",\"config\":{},\"inputs\":{}}]"
            + ",\"edges\":[{\"from\":\"trigger\",\"to\":\"callback\"},{\"from\":\"callback\",\"to\":\"end\"}]},\"activations\":[{\"typeKey\":\"trigger.pr.merged\",\"enabled\":true,\"config\":{}}]}";

        var response = await _factory.CreateClient().SendAsync(Authed(HttpMethod.Post, "/api/workflows", seed.UserId, seed.TeamId, json));
        var body = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.OK, customMessage: $"seeding a workflow over POST /api/workflows failed: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>The engine, in-process: the factory's job client is a no-op, so nothing else would ever execute the run.</summary>
    private async Task RunEngineAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowEngine>().ExecuteRunAsync(runId, CancellationToken.None);
    }

    /// <summary>The window passing, without waiting it out: every debounce claim on this seed's repository ends a minute ago.</summary>
    private async Task ExpireDebounceAsync(RepositorySeed seed)
    {
        using var scope = _factory.Services.CreateScope();
        var pattern = $"pr-debounce:%:{seed.RepositoryId:N}:%";
        await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().Database.ExecuteSqlInterpolatedAsync($"UPDATE webhook_claim SET claimed_at = now() - interval '2 minutes', expires_at = now() - interval '1 minute' WHERE claim_key LIKE {pattern}");
    }

    // ─── Seeds ─────────────────────────────────────────────────────────────────

    private sealed record RepositorySeed(Guid UserId, Guid TeamId, Guid RepositoryId, Guid WebhookId, string Secret);

    private Task<RepositorySeed> SeedGitHubRepositoryAsync(RepositoryVisibility visibility) => SeedRepositoryAsync(ProviderKind.GitHub, $"https://gh-{Guid.NewGuid():N}.invalid", $"ext-{Guid.NewGuid():N}", visibility);

    private Task<RepositorySeed> SeedGitLabRepositoryAsync(string baseUrl, string projectId) => SeedRepositoryAsync(ProviderKind.GitLab, baseUrl, projectId, RepositoryVisibility.Public);

    private async Task<RepositorySeed> SeedRepositoryAsync(ProviderKind provider, string baseUrl, string externalId, RepositoryVisibility visibility)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>();
        var encryptor = scope.ServiceProvider.GetRequiredService<IPayloadEncryptor>();
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var seed = new RepositorySeed(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"sec-{Guid.NewGuid():N}");
        var instanceId = Guid.NewGuid();
        var credentialId = Guid.NewGuid();

        db.User.Add(new User { Id = seed.UserId, SecurityStamp = TestToken.SeedStamp, Email = $"owner-{suffix}@test.local", Name = "Owner", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Team.Add(new Team { Id = seed.TeamId, Slug = $"team-{suffix}", Name = "Team", Kind = TeamKind.Workspace, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.TeamMembership.Add(new TeamMembership { Id = Guid.NewGuid(), TeamId = seed.TeamId, UserId = seed.UserId, Role = TeamRole.Owner, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.ProviderInstance.Add(new ProviderInstance { Id = instanceId, TeamId = seed.TeamId, Provider = provider, DisplayName = "P", BaseUrl = baseUrl, ApiUrl = baseUrl, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Credential.Add(new Credential { Id = credentialId, TeamId = seed.TeamId, ProviderInstanceId = instanceId, AuthType = AuthType.Pat, DisplayName = "PAT", EncryptedPayload = encryptor.Encrypt("{\"token\":\"fake-token\"}"), CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.Repository.Add(new Repository { Id = seed.RepositoryId, TeamId = seed.TeamId, ProviderInstanceId = instanceId, CredentialId = credentialId, ExternalId = externalId, NamespacePath = "acme", Name = "api", FullPath = $"acme/api-{suffix}", Visibility = visibility, WebUrl = "https://p.invalid/acme/api", CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });
        db.RepositoryWebhook.Add(new RepositoryWebhook { Id = seed.WebhookId, RepositoryId = seed.RepositoryId, ExternalId = "wh-1", CallbackUrl = $"https://x/cb/{suffix}", SecretEnc = encryptor.Encrypt(seed.Secret), Active = true, SubscribedEvents = new List<string> { "*" }, RegistrationStatus = RepositoryWebhookRegistrationStatus.Registered, CreatedBy = SystemUsers.SeederId, LastModifiedBy = SystemUsers.SeederId });

        await db.SaveChangesAsync();
        return seed;
    }

    // ─── Reads ─────────────────────────────────────────────────────────────────

    private async Task<int> CountRunsAsync(Guid workflowId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().CountAsync(r => r.WorkflowId == workflowId);
    }

    private async Task<JsonElement> LoadOnlyRunPayloadAsync(Guid workflowId)
    {
        using var scope = _factory.Services.CreateScope();
        var payloads = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRun.AsNoTracking()
            .Where(r => r.WorkflowId == workflowId)
            .Select(r => r.RunRequest.NormalizedPayloadJson)
            .ToListAsync();

        payloads.Count.ShouldBe(1, customMessage: "expected exactly one run for this workflow");
        return JsonDocument.Parse(payloads[0]).RootElement;
    }

    private async Task<List<string>> LoadRunPayloadsAsync(Guid workflowId, string field)
    {
        using var scope = _factory.Services.CreateScope();
        var payloads = await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.WorkflowId == workflowId).Select(r => r.RunRequest.NormalizedPayloadJson).ToListAsync();
        return payloads.Select(p => JsonDocument.Parse(p).RootElement.GetProperty(field).GetString()!).ToList();
    }

    private async Task<Guid> LoadOnlyRunIdAsync(Guid workflowId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRun.AsNoTracking().Where(r => r.WorkflowId == workflowId).Select(r => r.Id).SingleAsync();
    }

    private async Task<string> LoadCallbackTokenAsync(Guid runId)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRunWait.AsNoTracking().Where(w => w.RunId == runId && w.WaitKind == WorkflowWaitKinds.Callback).Select(w => w.Token).SingleAsync();
    }

    private async Task<List<string>> LoadRefusalsAsync(RepositorySeed seed) => (await LoadRejectedRowsAsync(seed)).Select(r => r.Error!).ToList();

    private async Task<List<WorkflowRunRequest>> LoadRejectedRowsAsync(RepositorySeed seed)
    {
        using var scope = _factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<CodeSpaceDbContext>().WorkflowRunRequest.AsNoTracking()
            .Where(r => r.TeamId == seed.TeamId && r.Status == WorkflowRunRequestStatus.Rejected && r.Error != null && !r.Error.StartsWith(WorkflowRunRequestRejectionReasons.NoMatchingActivation))
            .ToListAsync();
    }
}

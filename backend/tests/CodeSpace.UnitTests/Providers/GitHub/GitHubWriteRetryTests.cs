using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.Markdown;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers.GitHub;

/// <summary>
/// The real <see cref="GitHubRepositoryProvider"/> — Octokit, the wire, the resilience wrapper — against a loopback
/// GitHub that applies each write before it answers. When the answer to a write that LANDED is lost (a gateway
/// 502, a dropped connection), the retry must find that write instead of sending it again; when the write never
/// landed, the retry sends it again and it lands once. Either way the provider holds exactly one effect.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitHubWriteRetryTests : IDisposable
{
    private readonly StubProviderHost _github = new();

    public void Dispose() => _github.Dispose();

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.LandsThenConnectionDrops, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task PostComment_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        var comments = new ForgeCollection(scenario, CommentJson);
        _github.Answer("POST", "/repos/acme/api/issues/7/comments", comments.Create).Answer("GET", "/repos/acme/api/issues/7/comments", comments.List);

        var comment = await Provider().PostCommentAsync(Context(), Repository, 7, "Looks good.", CancellationToken.None);

        var landed = comments.Stored.ShouldHaveSingleItem("a comment that landed must not be posted again");
        comment.ExternalId.ShouldBe(landed.Id.ToString());
        comment.Body.ShouldBe("Looks good.", "CodeSpace shows what was written — the marker stays on GitHub's copy only");
        landed.Text("body").ShouldCarryOneMarker("Looks good.");
        _github.Sent("POST", "/repos/acme/api/issues/7/comments").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.LandsThenConnectionDrops, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task CommentIssue_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        var comments = new ForgeCollection(scenario, CommentJson);
        _github.Answer("POST", "/repos/acme/api/issues/5/comments", comments.Create).Answer("GET", "/repos/acme/api/issues/5/comments", comments.List);

        var comment = await Provider().CommentIssueAsync(Context(), Repository, 5, "Reproduced on main.", CancellationToken.None);

        var landed = comments.Stored.ShouldHaveSingleItem("a comment that landed must not be posted again");
        comment.ExternalId.ShouldBe(landed.Id.ToString());
        comment.Body.ShouldBe("Reproduced on main.", "CodeSpace shows what was written — the marker stays on GitHub's copy only");
        landed.Text("body").ShouldCarryOneMarker("Reproduced on main.");
        _github.Sent("POST", "/repos/acme/api/issues/5/comments").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.LandsThenConnectionDrops, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task CreateIssue_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        var issues = new ForgeCollection(scenario, IssueJson);
        _github.Answer("POST", "/repos/acme/api/issues", issues.Create).Answer("GET", "/repos/acme/api/issues?", issues.List);

        var issue = await Provider().CreateIssueAsync(Context(), Repository, new CreateIssueInput { Title = "Flaky retry", Body = "Seen twice this week." }, CancellationToken.None);

        var landed = issues.Stored.ShouldHaveSingleItem("an issue that landed must not be opened again");
        issue.Number.ShouldBe((int)landed.Id);
        issue.Body.ShouldBe("Seen twice this week.", "CodeSpace shows what was written — the marker stays on GitHub's copy only");
        landed.Text("body").ShouldCarryOneMarker("Seen twice this week.");
        _github.Sent("POST", "/repos/acme/api/issues").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.LandsThenConnectionDrops, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task OpenPullRequest_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        // GitHub itself refuses a second OPEN pull request for the same head → base with 422, so a blind re-send
        // cannot duplicate one — it can only be refused and fall back to binding. Found first, it is never re-sent.
        var pulls = new ForgeCollection(scenario, PullRequestJson, refuse: RefuseSecondOpenPullRequest);
        _github.Answer("POST", "/repos/acme/api/pulls", pulls.Create).Answer("GET", "/repos/acme/api/pulls?", pulls.List);

        var pr = await Provider().OpenPullRequestAsync(Context(), Repository, new OpenPullRequestInput { Title = "Retry safely", SourceBranch = "feature/retry", TargetBranch = "main", Body = "Probes before re-sending." }, CancellationToken.None);

        var landed = pulls.Stored.ShouldHaveSingleItem();
        pr.Number.ShouldBe((int)landed.Id);
        pr.TargetBranch.ShouldBe("main");
        landed.Text("body").ShouldBe("Probes before re-sending.", "the head → base pair already identifies the pull request; its body is sent as written");
        _github.Sent("POST", "/repos/acme/api/pulls").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.LandsThenConnectionDrops, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task SubmitReview_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        var reviews = new ForgeCollection(scenario, ReviewJson);
        _github.Answer("POST", "/repos/acme/api/pulls/7/reviews", reviews.Create).Answer("GET", "/repos/acme/api/pulls/7/reviews", reviews.List);

        var review = await Provider().SubmitReviewAsync(Context(), Repository, 7, PullRequestReviewVerdict.RequestChanges, "Please add a test.", CancellationToken.None);

        var landed = reviews.Stored.ShouldHaveSingleItem("a review that landed must not be submitted again");
        review.ExternalId.ShouldBe(landed.Id.ToString());
        landed.Text("body").ShouldCarryOneMarker("Please add a test.");
        _github.Sent("POST", "/repos/acme/api/pulls/7/reviews").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.LandsThenConnectionDrops, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task Merge_merges_exactly_once_and_still_deletes_the_source_branch(WriteScenario scenario, int expectedMerges)
    {
        var pull = new ForgeMergeablePullRequest(scenario);
        _github.Answer("PUT", "/repos/acme/api/pulls/7/merge", pull.Merge).Answer("GET", "/repos/acme/api/pulls/7", pull.Get).Answer("DELETE", "/repos/acme/api/git/refs/heads/feature/retry", pull.DeleteBranch);

        var result = await Provider().MergePullRequestAsync(Context(), Repository, 7, new MergePullRequestInput { Method = PullRequestMergeMethod.Squash, DeleteSourceBranch = true }, CancellationToken.None);

        result.Merged.ShouldBeTrue("the merge landed — a second merge call would be refused as 'not mergeable' and fail a merge that succeeded");
        result.Sha.ShouldBe(ForgeMergeablePullRequest.MergeSha);
        _github.Sent("PUT", "/repos/acme/api/pulls/7/merge").ShouldBe(expectedMerges);
        pull.BranchDeletes.ShouldBe(1, "the caller asked for the source branch to go; finding the merge already done must not skip that");
    }

    [Fact]
    public async Task Two_concurrent_comments_with_the_same_text_each_adopt_their_own()
    {
        var comments = new ForgeCollection(attempt => attempt < 2 ? AttemptOutcome.LandsThenGatewayError : AttemptOutcome.Lands, CommentJson);
        _github.Answer("POST", "/repos/acme/api/issues/7/comments", comments.Create).Answer("GET", "/repos/acme/api/issues/7/comments", comments.List);
        var provider = Provider();

        var both = await Task.WhenAll(provider.PostCommentAsync(Context(), Repository, 7, "Same words.", CancellationToken.None), provider.PostCommentAsync(Context(), Repository, 7, "Same words.", CancellationToken.None));

        comments.Stored.Count.ShouldBe(2);
        both.Select(c => c.ExternalId).ShouldBe(comments.Stored.Select(s => s.Id.ToString()), ignoreOrder: true, customMessage: "each call must adopt the comment IT made — the text is identical, only the marker tells them apart");
        _github.Sent("POST", "/repos/acme/api/issues/7/comments").ShouldBe(2);
    }

    [Fact]
    public async Task OpenPullRequest_adopts_the_pull_request_for_its_own_base_when_the_head_has_two()
    {
        // feature/retry is already open into release. This call opens feature/retry → main; its create lands and the
        // answer is lost. A one-item page filtered by head alone hands back the release pull request, and the pull
        // request this call did open is reported as a failure.
        var pulls = new ForgeCollection(WriteScenario.LandsThenGatewayError, PullRequestJson, refuse: RefuseSecondOpenPullRequest);
        pulls.Seed(new { title = "Release train", head = "feature/retry", @base = "release", body = (string?)null });
        _github.Answer("POST", "/repos/acme/api/pulls", pulls.Create).Answer("GET", "/repos/acme/api/pulls?", request => ListOpenPullRequests(pulls, request));

        var pr = await Provider().OpenPullRequestAsync(Context(), Repository, new OpenPullRequestInput { Title = "Retry safely", SourceBranch = "feature/retry", TargetBranch = "main" }, CancellationToken.None);

        pr.TargetBranch.ShouldBe("main");
        pr.Number.ShouldBe((int)pulls.Stored.Single(p => p.Text("base") == "main").Id);
        _github.Sent("POST", "/repos/acme/api/pulls").ShouldBe(1);
    }

    [Fact]
    public async Task Reads_show_what_was_written_not_the_marker()
    {
        var marker = IdempotencyMarker.New();
        _github.Answer("GET", "/repos/acme/api/issues/5/comments", 200, "[" + CommentJson(11, IdempotencyMarker.Append("Reproduced on main.", marker)) + "]")
            .Answer("GET", "/repos/acme/api/issues/5", 200, IssueJson(5, "Flaky retry", IdempotencyMarker.Append("Seen twice this week.", marker)))
            .Answer("GET", "/repos/acme/api/issues/6", 200, IssueJson(6, "No details", IdempotencyMarker.Append(null, marker)));
        var provider = Provider();

        var issue = await provider.GetIssueAsync(Context(), Repository, 5, CancellationToken.None);
        var bodiless = await provider.GetIssueAsync(Context(), Repository, 6, CancellationToken.None);
        var comments = await provider.ListIssueCommentsAsync(Context(), Repository, 5, CancellationToken.None);

        issue.Body.ShouldBe("Seen twice this week.");
        bodiless.Body.ShouldBeEmpty("an issue opened without a body must reach the page's placeholder, not the marker");
        comments.ShouldHaveSingleItem().Body.ShouldBe("Reproduced on main.");
    }

    [Theory]
    [InlineData(RepositoryHooks, WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(RepositoryHooks, WriteScenario.LandsThenConnectionDrops, 1)]
    [InlineData(RepositoryHooks, WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(RepositoryHooks, WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    [InlineData(OrganizationHooks, WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(OrganizationHooks, WriteScenario.LandsThenConnectionDrops, 1)]
    [InlineData(OrganizationHooks, WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(OrganizationHooks, WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task RegisterWebhook_registers_exactly_one_hook(string hooksPath, WriteScenario scenario, int expectedCreates)
    {
        // GitHub refuses a second hook at a URL one already has with 422, so re-sending a create that landed does not
        // duplicate the hook — it reports a registration that succeeded as failed, and the registrar backs off.
        var hooks = new ForgeCollection(scenario, HookJson, refuse: RefuseSecondHookAtOneUrl);
        _github.Answer("POST", hooksPath, hooks.Create).Answer("GET", hooksPath, hooks.List);

        var hook = await RegisterWebhookAsync(hooksPath);

        var landed = hooks.Stored.ShouldHaveSingleItem("a hook that landed must not be created again");
        hook.ExternalId.ShouldBe(landed.Id.ToString());
        hook.CallbackUrl.ShouldBe(Registration.CallbackUrl);
        _github.Sent("POST", hooksPath).ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(RepositoryHooks)]
    [InlineData(OrganizationHooks)]
    public async Task RegisterWebhook_adopts_the_hook_GitHub_refuses_to_create_twice(string hooksPath)
    {
        // Between the registrar's lookup and this create, another run of the same registration made the hook (a stuck row
        // re-dispatched while the first run was still in flight). GitHub answers the create 422 "Hook already exists" —
        // the hook this registration asked for is there, so the registration succeeded.
        var hooks = new ForgeCollection(_ => AttemptOutcome.Lands, HookJson, refuse: RefuseSecondHookAtOneUrl);
        hooks.Seed(HookAt(Registration.CallbackUrl));
        _github.Answer("POST", hooksPath, hooks.Create).Answer("GET", hooksPath, hooks.List);

        var hook = await RegisterWebhookAsync(hooksPath);

        hook.ExternalId.ShouldBe(hooks.Stored.ShouldHaveSingleItem().Id.ToString(), "GitHub's refusal named a hook that is already there — that hook is the registration's result");
        _github.Sent("POST", hooksPath).ShouldBe(1);
    }

    [Theory]
    [InlineData(RepositoryHooks)]
    [InlineData(OrganizationHooks)]
    public async Task RegisterWebhook_still_fails_on_a_422_that_leaves_no_hook_at_its_url(string hooksPath)
    {
        // GitHub refuses this create for a reason of its own, and the only hook there is another registration's. Only a
        // hook at THIS registration's callback URL means the work is done; anything else leaves the refusal standing.
        var hooks = new ForgeCollection(_ => AttemptOutcome.Lands, HookJson, refuse: (_, _) => new StubReply(422, HookLimitRefusal));
        hooks.Seed(HookAt("https://codespace.test/api/webhooks/another-registration"));
        _github.Answer("POST", hooksPath, hooks.Create).Answer("GET", hooksPath, hooks.List);

        var failure = await Record.ExceptionAsync(() => RegisterWebhookAsync(hooksPath));

        var refused = failure.ShouldBeOfType<ProviderWebhookRegistrationException>();
        refused.Diagnostic.StatusCode.ShouldBe(422);
        refused.Diagnostic.ResponseBody.ShouldNotBeNull().ShouldContain("cannot have more than 20 hooks", Case.Insensitive, "the attempt row keeps GitHub's own words about why");
    }

    [Theory]
    [InlineData(RepositoryHooks, false)]
    [InlineData(RepositoryHooks, true)]
    [InlineData(OrganizationHooks, false)]
    [InlineData(OrganizationHooks, true)]
    public async Task A_probe_that_cannot_read_the_hooks_never_sends_the_create_again(string hooksPath, bool probeConnectionDrops)
    {
        // The create landed and its answer was lost to a 502; then the probe cannot read the hooks either. Octokit's 5xx
        // and a dropped connection (or a timeout) are transient, so the failed probe spends its attempt and is asked
        // again — never a re-send — and once the attempts are spent the registration fails for the registrar's next run.
        var hooks = new ForgeCollection(WriteScenario.LandsThenGatewayError, HookJson, refuse: RefuseSecondHookAtOneUrl);
        _github.Answer("POST", hooksPath, hooks.Create).Answer("GET", hooksPath, _ => probeConnectionDrops ? StubReply.DropConnection : new StubReply(502, """{"message":"Server Error"}"""));

        var failure = await Record.ExceptionAsync(() => RegisterWebhookAsync(hooksPath));

        failure.ShouldBeOfType<ProviderWebhookRegistrationException>();
        _github.Sent("POST", hooksPath).ShouldBe(1, "a probe that cannot tell is not 'nothing landed'");
        _github.Sent("GET", hooksPath).ShouldBe(ExternalCallResilience.MaxAttempts - 1);
    }

    // ── Loopback GitHub ──

    private static readonly RemoteRepository Repository = new()
    {
        ExternalId = "4242",
        NamespacePath = "acme",
        Name = "api",
        FullPath = "acme/api",
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = "https://github.test/acme/api"
    };

    private ProviderContext Context() => new(new ProviderInstance { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitHub, DisplayName = "loopback", BaseUrl = _github.BaseUrl, ApiUrl = _github.BaseUrl }, new Credential { Id = Guid.NewGuid(), AuthType = AuthType.Pat, DisplayName = "pat", EncryptedPayload = "unused" });

    private static GitHubRepositoryProvider Provider()
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitHubErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);
        var normalizer = new GitHubEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>()));

        return new GitHubRepositoryProvider(new StaticTokenAuth(), resilience, new GitHubSignatureVerifier(), normalizer, new GitHubWebhookRepositoryIdentifier());
    }

    private static StubReply? RefuseSecondOpenPullRequest(JsonElement sent, IReadOnlyList<ForgeCollection.StoredItem> stored) =>
        stored.Any(s => s.Text("head") == sent.GetProperty("head").GetString() && s.Text("base") == sent.GetProperty("base").GetString())
            ? new StubReply(422, """{"message":"Validation Failed","errors":[{"resource":"PullRequest","code":"custom","message":"A pull request already exists for acme:feature/retry."}]}""")
            : null;

    /// <summary>GitHub's list filters, honoured: head (owner:branch), base, and a page of per_page — so a probe that leaves a filter out gets the pull request GitHub would give it.</summary>
    private static StubReply ListOpenPullRequests(ForgeCollection pulls, RecordedRequest request)
    {
        var query = ForgeQuery.Of(request);
        var matching = pulls.Stored.Where(p => query["head"] is not { } head || head == $"acme:{p.Text("head")}").Where(p => query["base"] is not { } baseBranch || baseBranch == p.Text("base"));

        return new StubReply(200, "[" + string.Join(",", matching.Page(query).Select(p => PullRequestJson(p.Id, p.Sent))) + "]");
    }

    private static string CommentJson(long id, JsonElement sent) => CommentJson(id, sent.GetProperty("body").GetString());

    private static string CommentJson(long id, string? body) => JsonSerializer.Serialize(new
    {
        id,
        body,
        user = new { login = "codespace-bot" },
        created_at = "2026-09-24T08:00:00Z",
        html_url = $"https://github.test/acme/api/issues/7#issuecomment-{id}"
    });

    private static string IssueJson(long id, JsonElement sent) => IssueJson(id, sent.GetProperty("title").GetString()!, sent.TryGetProperty("body", out var body) ? body.GetString() : null);

    private static string IssueJson(long id, string title, string? body) => JsonSerializer.Serialize(new
    {
        id,
        number = id,
        title,
        body,
        state = "open",
        user = new { login = "codespace-bot" },
        labels = Array.Empty<object>(),
        assignees = Array.Empty<object>(),
        comments = 0,
        created_at = "2026-09-24T08:00:00Z",
        html_url = $"https://github.test/acme/api/issues/{id}"
    });

    private static string PullRequestJson(long id, JsonElement sent) => JsonSerializer.Serialize(new
    {
        id,
        number = id,
        title = sent.GetProperty("title").GetString(),
        body = sent.TryGetProperty("body", out var body) ? body.GetString() : null,
        state = "open",
        draft = false,
        merged = false,
        head = new { @ref = sent.GetProperty("head").GetString(), sha = "0a1b2c3d", label = $"acme:{sent.GetProperty("head").GetString()}" },
        @base = new { @ref = sent.GetProperty("base").GetString(), sha = "4e5f6a7b", label = $"acme:{sent.GetProperty("base").GetString()}" },
        user = new { login = "codespace-bot" },
        labels = Array.Empty<object>(),
        assignees = Array.Empty<object>(),
        requested_reviewers = Array.Empty<object>(),
        created_at = "2026-09-24T08:00:00Z",
        updated_at = "2026-09-24T08:00:00Z",
        html_url = $"https://github.test/acme/api/pull/{id}"
    });

    private static string ReviewJson(long id, JsonElement sent) => JsonSerializer.Serialize(new
    {
        id,
        body = sent.GetProperty("body").GetString(),
        state = "CHANGES_REQUESTED",
        user = new { login = "codespace-bot" },
        submitted_at = "2026-09-24T08:00:00Z",
        html_url = $"https://github.test/acme/api/pull/7#pullrequestreview-{id}"
    });

    private const string RepositoryHooks = "/repositories/4242/hooks";

    private const string OrganizationHooks = "/orgs/acme/hooks";

    private const string HookLimitRefusal = """{"message":"Validation Failed","errors":[{"resource":"Hook","code":"custom","message":"The \"push\" event cannot have more than 20 hooks"}]}""";

    private static readonly WebhookRegistration Registration = new() { CallbackUrl = "https://codespace.test/api/webhooks/0f5e2c7a-9b1d-4e3f-8a6b-2c4d6e8f0a1b", Secret = "whsec-loopback", SubscribedEvents = new[] { "push", "pull_request" } };

    /// <summary>The create the path names — a repository hook or an organization hook, the two GitHub refuses to repeat at one URL.</summary>
    private Task<RemoteWebhook> RegisterWebhookAsync(string hooksPath) => hooksPath == OrganizationHooks
        ? Provider().RegisterConnectionWebhookAsync(Context(), "acme", Registration, CancellationToken.None)
        : Provider().RegisterWebhookAsync(Context(), Repository, Registration, CancellationToken.None);

    /// <summary>GitHub's rule, honoured: a create for a URL a hook on the same owner already has is refused with 422 and changes nothing.</summary>
    private static StubReply? RefuseSecondHookAtOneUrl(JsonElement sent, IReadOnlyList<ForgeCollection.StoredItem> stored) =>
        stored.Any(s => string.Equals(HookUrl(s.Sent), HookUrl(sent), StringComparison.OrdinalIgnoreCase))
            ? new StubReply(422, """{"message":"Validation Failed","errors":[{"resource":"Hook","code":"custom","message":"Hook already exists on this repository"}]}""")
            : null;

    private static string? HookUrl(JsonElement sent) => sent.GetProperty("config").GetProperty("url").GetString();

    private static object HookAt(string callbackUrl) => new { name = "web", active = true, events = new[] { "*" }, config = new { url = callbackUrl, content_type = "json" } };

    private static string HookJson(long id, JsonElement sent) => JsonSerializer.Serialize(new
    {
        id,
        name = "web",
        active = true,
        events = new[] { "*" },
        config = new { url = HookUrl(sent), content_type = "json" },
        created_at = "2026-09-24T08:00:00Z",
        updated_at = "2026-09-24T08:00:00Z"
    });

    /// <summary>Pull request #7 on feature/retry. Each merge call follows the <see cref="WriteScenario"/>; one that lands merges it, and GitHub answers a merge of an already-merged pull request with 405.</summary>
    private sealed class ForgeMergeablePullRequest
    {
        public const string MergeSha = "9f8e7d6c5b4a";

        private readonly WriteScenario _scenario;
        private int _mergeCalls;
        private bool _merged;

        public ForgeMergeablePullRequest(WriteScenario scenario) { _scenario = scenario; }

        public int BranchDeletes { get; private set; }

        public StubReply Merge(RecordedRequest _)
        {
            var outcome = _scenario.OutcomeOf(_mergeCalls++);

            if (outcome == AttemptOutcome.Refused) return outcome.Answer(() => string.Empty);
            if (_merged) return new StubReply(405, """{"message":"Pull Request is not mergeable"}""");

            _merged = true;

            return outcome == AttemptOutcome.Lands ? new StubReply(200, JsonSerializer.Serialize(new { sha = MergeSha, merged = true, message = "Pull Request successfully merged" })) : outcome.Answer(() => string.Empty);
        }

        public StubReply Get(RecordedRequest _) => new(200, JsonSerializer.Serialize(new
        {
            id = 7007,
            number = 7,
            title = "Retry safely",
            state = _merged ? "closed" : "open",
            merged = _merged,
            merged_at = _merged ? "2026-09-24T08:00:00Z" : null,   // Octokit derives PullRequest.Merged from merged_at
            merge_commit_sha = _merged ? MergeSha : null,
            head = new { @ref = "feature/retry", sha = "0a1b2c3d" },
            @base = new { @ref = "main", sha = "4e5f6a7b" },
            user = new { login = "codespace-bot" },
            html_url = "https://github.test/acme/api/pull/7"
        }));

        public StubReply DeleteBranch(RecordedRequest _)
        {
            BranchDeletes++;
            return new StubReply(204, string.Empty);
        }
    }

    private sealed class StaticTokenAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "ghp_loopback" });
    }
}

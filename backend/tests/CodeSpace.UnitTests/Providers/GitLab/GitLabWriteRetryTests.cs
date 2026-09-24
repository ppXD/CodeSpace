using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Markdown;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers.GitLab;

/// <summary>
/// The real <see cref="GitLabRepositoryProvider"/> — NGitLab, the wire, the resilience wrapper — against a loopback
/// GitLab that applies each write before it answers. A gateway 502 after GitLab committed must not make the retry
/// apply the write again. (NGitLab surfaces a dropped connection as a WebException, which the wrapper never
/// retries, so the 5xx is the ambiguous failure that reaches a retry here.)
/// </summary>
[Trait("Category", "Unit")]
public sealed class GitLabWriteRetryTests : IDisposable
{
    private readonly StubProviderHost _gitlab = new();

    public void Dispose() => _gitlab.Dispose();

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task PostComment_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        var notes = new ForgeCollection(scenario, NoteJson);
        _gitlab.Answer("POST", "/api/v4/projects/4242/merge_requests/7/notes", notes.Create).Answer("GET", "/api/v4/projects/4242/merge_requests/7/notes", notes.List);

        var comment = await Provider().PostCommentAsync(Context(), Repository, 7, "Looks good.", CancellationToken.None);

        var landed = notes.Stored.ShouldHaveSingleItem("a note that landed must not be posted again");
        comment.ExternalId.ShouldBe(landed.Id.ToString());
        comment.Body.ShouldBe("Looks good.", "CodeSpace shows what was written — the marker stays on GitLab's copy only");
        landed.Text("body").ShouldCarryOneMarker("Looks good.");
        _gitlab.Sent("POST", "/api/v4/projects/4242/merge_requests/7/notes").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task CommentIssue_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        var notes = new ForgeCollection(scenario, NoteJson);
        _gitlab.Answer("POST", "/api/v4/projects/4242/issues/5/notes", notes.Create).Answer("GET", "/api/v4/projects/4242/issues/5/notes", notes.List);

        var comment = await Provider().CommentIssueAsync(Context(), Repository, 5, "Reproduced on main.", CancellationToken.None);

        var landed = notes.Stored.ShouldHaveSingleItem("a note that landed must not be posted again");
        comment.ExternalId.ShouldBe(landed.Id.ToString());
        comment.Body.ShouldBe("Reproduced on main.", "CodeSpace shows what was written — the marker stays on GitLab's copy only");
        landed.Text("body").ShouldCarryOneMarker("Reproduced on main.");
        _gitlab.Sent("POST", "/api/v4/projects/4242/issues/5/notes").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task CreateIssue_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        var issues = new ForgeCollection(scenario, IssueJson);
        _gitlab.Answer("POST", "/api/v4/projects/4242/issues", issues.Create).Answer("GET", "/api/v4/projects/4242/issues?", issues.List);

        var issue = await Provider().CreateIssueAsync(Context(), Repository, new CreateIssueInput { Title = "Flaky retry", Body = "Seen twice this week." }, CancellationToken.None);

        var landed = issues.Stored.ShouldHaveSingleItem("an issue that landed must not be opened again");
        issue.Number.ShouldBe((int)landed.Id);
        issue.Body.ShouldBe("Seen twice this week.", "CodeSpace shows what was written — the marker stays on GitLab's copy only");
        landed.Text("description").ShouldCarryOneMarker("Seen twice this week.");
        _gitlab.Sent("POST", "/api/v4/projects/4242/issues").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task OpenPullRequest_lands_exactly_once(WriteScenario scenario, int expectedCreates)
    {
        // GitLab refuses a second open merge request for one source → target pair with 409, so a blind re-send cannot
        // duplicate one — only be refused and fall back to binding. Found first, it is never re-sent.
        var mergeRequests = new ForgeCollection(scenario, MergeRequestJson, refuse: RefuseSecondOpenMergeRequest);
        _gitlab.Answer("POST", "/api/v4/projects/4242/merge_requests", mergeRequests.Create).Answer("GET", "/api/v4/projects/4242/merge_requests?", mergeRequests.List).Answer("GET", "/api/v4/projects/4242/labels", 200, "[]");

        var pr = await Provider().OpenPullRequestAsync(Context(), Repository, new OpenPullRequestInput { Title = "Retry safely", SourceBranch = "feature/retry", TargetBranch = "main", Body = "Probes before re-sending." }, CancellationToken.None);

        var landed = mergeRequests.Stored.ShouldHaveSingleItem();
        pr.Number.ShouldBe((int)landed.Id);
        pr.TargetBranch.ShouldBe("main");
        landed.Text("description").ShouldBe("Probes before re-sending.", "the source → target pair already identifies the merge request; its description is sent as written");
        _gitlab.Sent("POST", "/api/v4/projects/4242/merge_requests").ShouldBe(expectedCreates);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task Merge_merges_exactly_once(WriteScenario scenario, int expectedMerges)
    {
        var mergeRequest = new ForgeMergeableMergeRequest(scenario);
        _gitlab.Answer("PUT", "/api/v4/projects/4242/merge_requests/7/merge", mergeRequest.Merge).Answer("GET", "/api/v4/projects/4242/merge_requests/7", mergeRequest.Get);

        var result = await Provider().MergePullRequestAsync(Context(), Repository, 7, new MergePullRequestInput { Method = PullRequestMergeMethod.Squash }, CancellationToken.None);

        result.Merged.ShouldBeTrue("the merge landed — a second accept is refused with 405 and would fail a merge that succeeded");
        result.Sha.ShouldBe(ForgeMergeableMergeRequest.MergeSha);
        _gitlab.Sent("PUT", "/api/v4/projects/4242/merge_requests/7/merge").ShouldBe(expectedMerges);
    }

    [Theory]
    [InlineData(WriteScenario.LandsThenGatewayError, 1)]
    [InlineData(WriteScenario.RefusedBeforeLanding, 2)]
    [InlineData(WriteScenario.RefusedThenLandsThenGatewayError, 2)]
    public async Task SubmitReview_approves_exactly_once(WriteScenario scenario, int expectedApproves)
    {
        // GitLab answers a second approve by the same user with a bare 401 — re-sending an approve that landed
        // fails a review that succeeded. The review note is already an upsert keyed on its own marker.
        var approvals = new ForgeApprovals(scenario);
        var notes = new ForgeCollection(_ => AttemptOutcome.Lands, NoteJson);
        _gitlab.Answer("GET", "/api/v4/projects/4242/merge_requests/7/approvals", approvals.Get).Answer("POST", "/api/v4/projects/4242/merge_requests/7/approve", approvals.Approve).Answer("POST", "/api/v4/projects/4242/merge_requests/7/notes", notes.Create).Answer("GET", "/api/v4/projects/4242/merge_requests/7/notes", notes.List);

        var review = await Provider().SubmitReviewAsync(Context(), Repository, 7, PullRequestReviewVerdict.Approve, "Ship it.", CancellationToken.None);

        approvals.Approved.ShouldBeTrue();
        _gitlab.Sent("POST", "/api/v4/projects/4242/merge_requests/7/approve").ShouldBe(expectedApproves);
        review.ExternalId.ShouldBe(notes.Stored.ShouldHaveSingleItem().Id.ToString());
    }

    [Fact]
    public async Task OpenPullRequest_adopts_the_merge_request_for_its_own_target_when_the_source_has_two()
    {
        // feature/retry is already open into release. This call opens feature/retry → main; its create lands and the
        // answer is lost. A one-item page filtered by source alone hands back the release merge request, and the
        // merge request this call did open is reported as a failure.
        var mergeRequests = new ForgeCollection(WriteScenario.LandsThenGatewayError, MergeRequestJson, refuse: RefuseSecondOpenMergeRequest);
        mergeRequests.Seed(new { title = "Release train", source_branch = "feature/retry", target_branch = "release", description = (string?)null });
        _gitlab.Answer("POST", "/api/v4/projects/4242/merge_requests", mergeRequests.Create).Answer("GET", "/api/v4/projects/4242/merge_requests?", request => ListOpenMergeRequests(mergeRequests, request)).Answer("GET", "/api/v4/projects/4242/labels", 200, "[]");

        var pr = await Provider().OpenPullRequestAsync(Context(), Repository, new OpenPullRequestInput { Title = "Retry safely", SourceBranch = "feature/retry", TargetBranch = "main" }, CancellationToken.None);

        pr.TargetBranch.ShouldBe("main");
        pr.Number.ShouldBe((int)mergeRequests.Stored.Single(m => m.Text("target_branch") == "main").Id);
        _gitlab.Sent("POST", "/api/v4/projects/4242/merge_requests").ShouldBe(1);
    }

    [Fact]
    public async Task Reads_show_what_was_written_not_the_marker()
    {
        var marker = IdempotencyMarker.New();
        _gitlab.Answer("GET", "/api/v4/projects/4242/issues/5/notes", 200, "[" + NoteJson(11, IdempotencyMarker.Append("Reproduced on main.", marker)) + "]")
            .Answer("GET", "/api/v4/projects/4242/issues/5", 200, IssueJson(5, "Flaky retry", IdempotencyMarker.Append("Seen twice this week.", marker)))
            .Answer("GET", "/api/v4/projects/4242/issues/6", 200, IssueJson(6, "No details", IdempotencyMarker.Append(null, marker)));
        var provider = Provider();

        var issue = await provider.GetIssueAsync(Context(), Repository, 5, CancellationToken.None);
        var bodiless = await provider.GetIssueAsync(Context(), Repository, 6, CancellationToken.None);
        var comments = await provider.ListIssueCommentsAsync(Context(), Repository, 5, CancellationToken.None);

        issue.Body.ShouldBe("Seen twice this week.");
        bodiless.Body.ShouldBeEmpty("an issue opened without a description must reach the page's placeholder, not the marker");
        comments.ShouldHaveSingleItem().Body.ShouldBe("Reproduced on main.");
    }

    // ── Loopback GitLab ──

    private static readonly RemoteRepository Repository = new()
    {
        ExternalId = "4242",
        NamespacePath = "acme",
        Name = "api",
        FullPath = "acme/api",
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = "https://gitlab.test/acme/api"
    };

    private ProviderContext Context() => new(new ProviderInstance { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = ProviderKind.GitLab, DisplayName = "loopback", BaseUrl = _gitlab.BaseUrl }, new Credential { Id = Guid.NewGuid(), AuthType = AuthType.Pat, DisplayName = "pat", EncryptedPayload = "unused" });

    private static GitLabRepositoryProvider Provider()
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitLabErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);
        var normalizer = new GitLabEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>()));

        return new GitLabRepositoryProvider(new StaticTokenAuth(), resilience, new GitLabSignatureVerifier(), normalizer, new GitLabWebhookRepositoryIdentifier());
    }

    private static StubReply? RefuseSecondOpenMergeRequest(JsonElement sent, IReadOnlyList<ForgeCollection.StoredItem> stored) =>
        stored.Any(s => s.Text("source_branch") == sent.GetProperty("source_branch").GetString() && s.Text("target_branch") == sent.GetProperty("target_branch").GetString())
            ? new StubReply(409, """{"message":["Another open merge request already exists for this source branch: !1001"]}""")
            : null;

    private static readonly object Author = new { id = 1, username = "codespace-bot", name = "CodeSpace" };

    /// <summary>GitLab's list filters, honoured: source_branch, target_branch, and a page of per_page — so a probe that leaves a filter out gets the merge request GitLab would give it.</summary>
    private static StubReply ListOpenMergeRequests(ForgeCollection mergeRequests, RecordedRequest request)
    {
        var query = ForgeQuery.Of(request);
        var matching = mergeRequests.Stored.Where(m => query["source_branch"] is not { } source || source == m.Text("source_branch")).Where(m => query["target_branch"] is not { } target || target == m.Text("target_branch"));

        return new StubReply(200, "[" + string.Join(",", matching.Page(query).Select(m => MergeRequestJson(m.Id, m.Sent))) + "]");
    }

    private static string NoteJson(long id, JsonElement sent) => NoteJson(id, sent.GetProperty("body").GetString());

    private static string NoteJson(long id, string? body) => JsonSerializer.Serialize(new
    {
        id,
        body,
        author = Author,
        created_at = "2026-09-24T08:00:00.000Z",
        system = false
    });

    private static string IssueJson(long id, JsonElement sent) => IssueJson(id, sent.GetProperty("title").GetString()!, sent.TryGetProperty("description", out var description) ? description.GetString() : null);

    private static string IssueJson(long id, string title, string? description) => JsonSerializer.Serialize(new
    {
        id,
        iid = id,
        project_id = 4242,
        title,
        description,
        state = "opened",
        author = Author,
        labels = Array.Empty<string>(),
        assignees = Array.Empty<object>(),
        user_notes_count = 0,
        created_at = "2026-09-24T08:00:00.000Z",
        web_url = $"https://gitlab.test/acme/api/-/issues/{id}"
    });

    private static string MergeRequestJson(long id, JsonElement sent) => JsonSerializer.Serialize(new
    {
        id,
        iid = id,
        project_id = 4242,
        title = sent.GetProperty("title").GetString(),
        description = sent.TryGetProperty("description", out var description) ? description.GetString() : null,
        state = "opened",
        source_branch = sent.GetProperty("source_branch").GetString(),
        target_branch = sent.GetProperty("target_branch").GetString(),
        author = Author,
        labels = Array.Empty<string>(),
        assignees = Array.Empty<object>(),
        reviewers = Array.Empty<object>(),
        user_notes_count = 0,
        created_at = "2026-09-24T08:00:00.000Z",
        updated_at = "2026-09-24T08:00:00.000Z",
        web_url = $"https://gitlab.test/acme/api/-/merge_requests/{id}"
    });

    /// <summary>Merge request !7. Each accept follows the <see cref="WriteScenario"/>; one that lands merges it, and GitLab answers an accept of an already-merged merge request with 405.</summary>
    private sealed class ForgeMergeableMergeRequest
    {
        public const string MergeSha = "9f8e7d6c5b4a";

        private readonly WriteScenario _scenario;
        private int _mergeCalls;
        private bool _merged;

        public ForgeMergeableMergeRequest(WriteScenario scenario) { _scenario = scenario; }

        public StubReply Merge(RecordedRequest request)
        {
            var outcome = _scenario.OutcomeOf(_mergeCalls++);

            if (outcome == AttemptOutcome.Refused) return outcome.Answer(() => string.Empty);
            if (_merged) return new StubReply(405, """{"message":"405 Method Not Allowed"}""");

            _merged = true;

            return outcome == AttemptOutcome.Lands ? Get(request) : outcome.Answer(() => string.Empty);
        }

        public StubReply Get(RecordedRequest _) => new(200, JsonSerializer.Serialize(new
        {
            id = 7007,
            iid = 7,
            project_id = 4242,
            title = "Retry safely",
            state = _merged ? "merged" : "opened",
            merge_commit_sha = _merged ? MergeSha : null,
            source_branch = "feature/retry",
            target_branch = "main",
            author = Author,
            created_at = "2026-09-24T08:00:00.000Z",
            updated_at = "2026-09-24T08:00:00.000Z",
            web_url = "https://gitlab.test/acme/api/-/merge_requests/7"
        }));
    }

    /// <summary>The approval state of merge request !7 for the calling user. Each approve follows the <see cref="WriteScenario"/>; one that lands records the approval, and a second is refused with GitLab's bare 401.</summary>
    private sealed class ForgeApprovals
    {
        private readonly WriteScenario _scenario;
        private int _approveCalls;

        public ForgeApprovals(WriteScenario scenario) { _scenario = scenario; }

        public bool Approved { get; private set; }

        public StubReply Approve(RecordedRequest request)
        {
            var outcome = _scenario.OutcomeOf(_approveCalls++);

            if (outcome == AttemptOutcome.Refused) return outcome.Answer(() => string.Empty);
            if (Approved) return new StubReply(401, """{"message":"401 Unauthorized"}""");

            Approved = true;

            return outcome == AttemptOutcome.Lands ? Get(request) : outcome.Answer(() => string.Empty);
        }

        public StubReply Get(RecordedRequest _) => new(200, JsonSerializer.Serialize(new { id = 7007, iid = 7, project_id = 4242, user_has_approved = Approved, user_can_approve = !Approved, approved_by = Array.Empty<object>() }));
    }

    private sealed class StaticTokenAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "glpat-loopback" });
    }
}

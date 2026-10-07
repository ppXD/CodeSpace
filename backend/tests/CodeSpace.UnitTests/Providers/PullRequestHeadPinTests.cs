using System.Text.Json;
using CodeSpace.Core.Persistence.Entities;
using CodeSpace.Core.Services.Providers;
using CodeSpace.Core.Services.Providers.Auth;
using CodeSpace.Core.Services.Providers.Errors;
using CodeSpace.Core.Services.Providers.Events;
using CodeSpace.Core.Services.Providers.GitHub;
using CodeSpace.Core.Services.Providers.GitLab;
using CodeSpace.Core.Services.Providers.Resilience;
using CodeSpace.IntegrationTests.Webhooks;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// A merge or a review pinned to the head a reviewer saw, through the real providers (Octokit and NGitLab, the wire, the
/// resilience wrapper) against a loopback forge: the pin is sent as the provider's own precondition (GitHub merge
/// <c>sha</c> and review <c>commit_id</c>, GitLab accept and approve <c>sha</c>), a head that moved is refused by the
/// provider and merges or approves nothing, and the pull-request read an approval card is built from reports the head
/// commit and the repository the head lives in — a fork's own path.
/// </summary>
[Trait("Category", "Unit")]
public sealed class PullRequestHeadPinTests : IDisposable
{
    private const string Head = "0a1b2c3d4e5f60718293a4b5c6d7e8f901234567";

    private readonly StubProviderHost _forge = new();

    public void Dispose() => _forge.Dispose();

    [Theory]
    [InlineData(ProviderKind.GitHub, "PUT", "/repos/acme/api/pulls/7/merge")]
    [InlineData(ProviderKind.GitLab, "PUT", "/api/v4/projects/4242/merge_requests/7/merge")]
    public async Task A_pinned_merge_sends_the_head_as_the_providers_own_precondition(ProviderKind kind, string method, string path)
    {
        _forge.Answer(method, path, 200, kind == ProviderKind.GitHub ? GitHubMergedJson : GitLabMergedJson);

        var result = await MergeAsync(kind, new MergePullRequestInput { ExpectedHeadSha = Head });

        result.Merged.ShouldBeTrue();
        var sent = JsonDocument.Parse(_forge.Requests.ShouldHaveSingleItem().Body).RootElement;
        sent.GetProperty("sha").GetString().ShouldBe(Head);
    }

    [Theory]
    [InlineData(ProviderKind.GitHub, "/repos/acme/api/pulls/7/merge")]
    [InlineData(ProviderKind.GitLab, "/api/v4/projects/4242/merge_requests/7/merge")]
    public async Task An_unpinned_merge_sends_no_sha_and_merges_whatever_the_head_is_as_before(ProviderKind kind, string path)
    {
        _forge.Answer("PUT", path, 200, kind == ProviderKind.GitHub ? GitHubMergedJson : GitLabMergedJson);

        await MergeAsync(kind, new MergePullRequestInput());

        var sent = JsonDocument.Parse(_forge.Requests.ShouldHaveSingleItem().Body).RootElement;
        (sent.TryGetProperty("sha", out var sha) && sha.ValueKind != JsonValueKind.Null).ShouldBeFalse(sent.GetRawText());
    }

    [Theory]
    [InlineData(ProviderKind.GitHub, "/repos/acme/api/pulls/7/merge", """{"message":"Head branch was modified. Review and try the merge again."}""")]
    [InlineData(ProviderKind.GitLab, "/api/v4/projects/4242/merge_requests/7/merge", """{"message":"SHA does not match HEAD of source branch: ffff"}""")]
    public async Task A_head_that_moved_is_refused_by_the_provider_with_409_and_is_not_sent_again(ProviderKind kind, string path, string refusal)
    {
        _forge.Answer("PUT", path, 409, refusal);

        var failure = await Should.ThrowAsync<ProviderApiException>(() => MergeAsync(kind, new MergePullRequestInput { ExpectedHeadSha = Head }));

        failure.StatusCode.ShouldBe(409);
        _forge.Requests.Count(r => r.Method == "PUT").ShouldBe(1, "a refused precondition is an answer, not a blip to retry");
    }

    [Theory]
    [InlineData(Head)]
    [InlineData(null)]   // unpinned: the review takes whatever the head is, as before
    public async Task GitHub_sends_a_pinned_head_as_the_reviews_commit(string? pinned)
    {
        _forge.Answer("POST", "/repos/acme/api/pulls/7/reviews", 200, GitHubReviewJson).Answer("GET", "/repos/acme/api/pulls/7/reviews", 200, "[]");

        await GitHubProvider().SubmitReviewAsync(Context(ProviderKind.GitHub), Repository, 7, new SubmitPullRequestReviewInput { Verdict = PullRequestReviewVerdict.Approve, ExpectedHeadSha = pinned }, CancellationToken.None);

        var sent = JsonDocument.Parse(_forge.Requests.ShouldHaveSingleItem().Body).RootElement;
        (sent.TryGetProperty("commit_id", out var commit) && commit.ValueKind == JsonValueKind.String ? commit.GetString() : null).ShouldBe(pinned, sent.GetRawText());
    }

    [Theory]
    [InlineData(Head)]
    [InlineData(null)]
    public async Task GitLab_sends_a_pinned_head_as_the_approvals_sha(string? pinned)
    {
        _forge.Answer("GET", "/api/v4/projects/4242/merge_requests/7/approvals", 200, GitLabApprovalsJson)
            .Answer("POST", "/api/v4/projects/4242/merge_requests/7/approve", 200, GitLabApprovalsJson)
            .Answer("GET", "/api/v4/projects/4242/merge_requests/7/notes", 200, "[]")
            .Answer("POST", "/api/v4/projects/4242/merge_requests/7/notes", 201, GitLabNoteJson);

        await GitLabProvider().SubmitReviewAsync(Context(ProviderKind.GitLab), Repository, 7, new SubmitPullRequestReviewInput { Verdict = PullRequestReviewVerdict.Approve, ExpectedHeadSha = pinned }, CancellationToken.None);

        var approve = JsonDocument.Parse(_forge.Requests.Single(r => r.Method == "POST" && r.PathAndQuery.EndsWith("/approve")).Body).RootElement;
        (approve.TryGetProperty("sha", out var sha) && sha.ValueKind == JsonValueKind.String ? sha.GetString() : null).ShouldBe(pinned, approve.GetRawText());
    }

    [Fact]
    public async Task GitLab_refuses_a_pinned_approval_whose_head_moved_with_409_and_no_note_is_posted()
    {
        _forge.Answer("GET", "/api/v4/projects/4242/merge_requests/7/approvals", 200, GitLabApprovalsJson)
            .Answer("POST", "/api/v4/projects/4242/merge_requests/7/approve", 409, """{"message":"SHA does not match HEAD of source branch: ffff"}""");

        var failure = await Should.ThrowAsync<ProviderApiException>(() => GitLabProvider().SubmitReviewAsync(Context(ProviderKind.GitLab), Repository, 7, new SubmitPullRequestReviewInput { Verdict = PullRequestReviewVerdict.Approve, Body = "Ship it.", ExpectedHeadSha = Head }, CancellationToken.None));

        failure.StatusCode.ShouldBe(409);
        _forge.Requests.ShouldNotContain(r => r.PathAndQuery.Contains("/notes"), "a refused approval submits nothing: the review note is never posted");
    }

    [Theory]
    [InlineData(true, "outsider/api")]
    [InlineData(false, null)]   // GitHub reports a fork deleted since the pull request was opened as no head repository
    public async Task GitHub_reports_the_head_commit_and_the_repository_the_head_lives_in(bool forkStillExists, string? expectedHeadRepository)
    {
        _forge.Answer("GET", "/repos/acme/api/pulls/7", 200, GitHubPullRequestJson(forkStillExists ? new { id = 9090, name = "api", full_name = "outsider/api", owner = new { login = "outsider" } } : null));

        var pr = await GitHubProvider().GetPullRequestAsync(Context(ProviderKind.GitHub), Repository, 7, CancellationToken.None);

        (pr.HeadSha, pr.HeadRepositoryFullPath, pr.SourceBranch, pr.TargetBranch).ShouldBe((Head, expectedHeadRepository, "release", "main"));
    }

    [Fact]
    public async Task GitLab_reports_a_same_project_head_as_this_repository_without_another_read()
    {
        _forge.Answer("GET", "/api/v4/projects/4242/merge_requests/7", 200, GitLabMergeRequestJson(sourceProjectId: 4242)).Answer("GET", "/api/v4/projects/4242/labels", 200, "[]");

        var pr = await GitLabProvider().GetPullRequestAsync(Context(ProviderKind.GitLab), Repository, 7, CancellationToken.None);

        (pr.HeadSha, pr.HeadRepositoryFullPath).ShouldBe((Head, "acme/api"));
        _forge.Requests.ShouldNotContain(r => r.PathAndQuery.StartsWith("/api/v4/projects/9090"), "no fork to name");
    }

    [Theory]
    [InlineData(200, "outsider/api")]
    [InlineData(404, null)]   // a fork the connection cannot read is not named — and is not this repository either
    public async Task GitLab_names_a_forks_head_by_reading_the_source_project(int forkReadStatus, string? expectedHeadRepository)
    {
        _forge.Answer("GET", "/api/v4/projects/4242/merge_requests/7", 200, GitLabMergeRequestJson(sourceProjectId: 9090))
            .Answer("GET", "/api/v4/projects/4242/labels", 200, "[]")
            .Answer("GET", "/api/v4/projects/9090", forkReadStatus, forkReadStatus == 200 ? """{"id":9090,"name":"api","path":"api","path_with_namespace":"outsider/api"}""" : """{"message":"404 Project Not Found"}""");

        var pr = await GitLabProvider().GetPullRequestAsync(Context(ProviderKind.GitLab), Repository, 7, CancellationToken.None);

        (pr.HeadSha, pr.HeadRepositoryFullPath).ShouldBe((Head, expectedHeadRepository));
    }

    // ── Loopback forge ──

    private static readonly string GitHubReviewJson = JsonSerializer.Serialize(new { id = 55, node_id = "R_1", body = "ok", state = "APPROVED", commit_id = Head, html_url = "https://forge.test/acme/api/pull/7#pullrequestreview-55", user = new { login = "codespace" } });

    private static readonly string GitLabApprovalsJson = JsonSerializer.Serialize(new { id = 7007, iid = 7, project_id = 4242, user_has_approved = false, user_can_approve = true, approved_by = Array.Empty<object>() });

    private static readonly string GitLabNoteJson = JsonSerializer.Serialize(new { id = 11, body = "ok", author = new { id = 2, username = "codespace", name = "CodeSpace" }, created_at = "2026-09-24T08:00:00.000Z", system = false });

    private static readonly string GitHubMergedJson = JsonSerializer.Serialize(new { sha = "9f8e7d6c5b4a", merged = true, message = "Pull Request successfully merged" });

    private static readonly string GitLabMergedJson = JsonSerializer.Serialize(new
    {
        id = 7007, iid = 7, project_id = 4242, source_project_id = 4242, target_project_id = 4242, title = "Retry safely", state = "merged",
        merge_commit_sha = "9f8e7d6c5b4a", source_branch = "release", target_branch = "main", author = new { id = 2, username = "outsider", name = "Outsider" },
        created_at = "2026-09-24T08:00:00.000Z", updated_at = "2026-09-24T08:00:00.000Z", web_url = "https://forge.test/acme/api/-/merge_requests/7",
    });

    private static string GitHubPullRequestJson(object? headRepository) => JsonSerializer.Serialize(new
    {
        id = 7007, number = 7, title = "Retry safely", state = "open",
        head = new { @ref = "release", sha = Head, repo = headRepository },
        @base = new { @ref = "main", sha = "4e5f6a7b", repo = new { id = 4242, name = "api", full_name = "acme/api", owner = new { login = "acme" } } },
        user = new { login = "outsider" }, html_url = "https://forge.test/acme/api/pull/7",
    });

    private static string GitLabMergeRequestJson(int sourceProjectId) => JsonSerializer.Serialize(new
    {
        id = 7007, iid = 7, project_id = 4242, source_project_id = sourceProjectId, target_project_id = 4242, title = "Retry safely", state = "opened",
        sha = Head, source_branch = "release", target_branch = "main", author = new { id = 2, username = "outsider", name = "Outsider" },
        created_at = "2026-09-24T08:00:00.000Z", updated_at = "2026-09-24T08:00:00.000Z", web_url = "https://forge.test/acme/api/-/merge_requests/7",
    });

    private static readonly RemoteRepository Repository = new()
    {
        ExternalId = "4242", NamespacePath = "acme", Name = "api", FullPath = "acme/api", DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private, WebUrl = "https://forge.test/acme/api",
    };

    private Task<RemotePullRequestMergeResult> MergeAsync(ProviderKind kind, MergePullRequestInput input) => kind == ProviderKind.GitHub
        ? GitHubProvider().MergePullRequestAsync(Context(kind), Repository, 7, input, CancellationToken.None)
        : GitLabProvider().MergePullRequestAsync(Context(kind), Repository, 7, input, CancellationToken.None);

    private ProviderContext Context(ProviderKind kind) => new(new ProviderInstance { Id = Guid.NewGuid(), TeamId = Guid.NewGuid(), Provider = kind, DisplayName = "loopback", BaseUrl = _forge.BaseUrl, ApiUrl = kind == ProviderKind.GitHub ? _forge.BaseUrl : null }, new Credential { Id = Guid.NewGuid(), AuthType = AuthType.Pat, DisplayName = "pat", EncryptedPayload = "unused" });

    private static GitHubRepositoryProvider GitHubProvider()
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitHubErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);

        return new GitHubRepositoryProvider(new StaticTokenAuth(), resilience, new GitHubSignatureVerifier(), new GitHubEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>())), new GitHubWebhookRepositoryIdentifier());
    }

    private static GitLabRepositoryProvider GitLabProvider()
    {
        var resilience = new ExternalCallResilience(new ProviderErrorMapperRegistry(new IProviderErrorMapper[] { new GitLabErrorMapper() }), NullLogger<ExternalCallResilience>.Instance);

        return new GitLabRepositoryProvider(new StaticTokenAuth(), resilience, new GitLabSignatureVerifier(), new GitLabEventNormalizer(new ProviderEventSubscriptionRegistry(Array.Empty<IProviderEventSubscription>())), new GitLabWebhookRepositoryIdentifier());
    }

    private sealed class StaticTokenAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "fake-loopback-token" });
    }
}

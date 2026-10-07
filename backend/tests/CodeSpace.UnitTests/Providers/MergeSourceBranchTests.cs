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
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using static CodeSpace.IntegrationTests.Webhooks.StubProviderHost;

namespace CodeSpace.UnitTests.Providers;

/// <summary>
/// Merging with <c>deleteSourceBranch</c> through the real providers (Octokit and NGitLab, the wire, the resilience wrapper)
/// against a loopback forge. GitHub never deletes a head branch on merge, so the provider deletes <c>heads/{head.ref}</c>
/// itself, in the BASE repository, the only one its credential is for. That ref is the pull request's branch only when
/// the head lives in the base repository. A fork's head names a branch in the fork, and the base's branch of the same name
/// (release, a teammate's feature) belongs to someone else. GitLab deletes server-side, from the merge request's own
/// source project, so the provider only asks.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MergeSourceBranchTests : IDisposable
{
    private readonly StubProviderHost _forge = new();

    public void Dispose() => _forge.Dispose();

    [Theory]
    [InlineData(DeleteAnswer.Deletes, 1)]
    [InlineData(DeleteAnswer.DeletesThenGatewayError, 1)]
    [InlineData(DeleteAnswer.DeletesThenConnectionDrops, 2)]
    [InlineData(DeleteAnswer.AlreadyGone, 1)]
    public async Task GitHub_deletes_a_head_branch_that_lives_in_the_base_repository(DeleteAnswer answer, int expectedDeletes)
    {
        // However GitHub answers the DELETE, what is reported is what is left: a delete whose answer was lost after it
        // landed, or a branch the repository's own auto-delete already removed, leaves the branch gone, as asked.
        var github = new LoopbackGitHub(_forge, "feature/retry", Acme, answer);

        var result = await MergeOnGitHubAsync(deleteSourceBranch: true);

        result.Merged.ShouldBeTrue();
        result.SourceBranchDeletion.ShouldBe(SourceBranchDeletion.Deleted, result.SourceBranchDetail);
        result.SourceBranchDetail.ShouldBe("Deleted 'feature/retry' from acme/api.");
        github.BranchExists.ShouldBeFalse();
        _forge.Sent("DELETE", "/repos/acme/api/git/refs/heads/feature/retry").ShouldBe(expectedDeletes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GitHub_keeps_a_fork_s_head_and_never_deletes_the_base_branch_of_the_same_name(bool forkStillExists)
    {
        // An outsider named their fork branch after acme/api's real release branch. GitHub reports a fork deleted since the
        // pull request was opened as head.repo = null, and an unknown head repository is not the base repository either.
        var github = new LoopbackGitHub(_forge, "release", forkStillExists ? Outsider : null, DeleteAnswer.Deletes);

        var result = await MergeOnGitHubAsync(deleteSourceBranch: true);

        result.Merged.ShouldBeTrue("keeping the branch never undoes the merge");
        result.SourceBranchDeletion.ShouldBe(SourceBranchDeletion.SkippedFork);
        result.SourceBranchDetail.ShouldBe(forkStillExists ? "Kept 'release': the pull request's head is in outsider/api, not acme/api, and a source branch is deleted only from its own repository." : "Kept 'release': the pull request's head is in a repository GitHub no longer reports, not acme/api, and a source branch is deleted only from its own repository.");
        github.BranchExists.ShouldBeTrue("acme/api's release is not the pull request's branch");
        _forge.Requests.ShouldNotContain(r => r.Method == "DELETE", "the base repository's credential deletes nothing for a fork's pull request");
    }

    [Fact]
    public async Task GitHub_reports_a_refused_delete_and_the_merge_still_stands()
    {
        var github = new LoopbackGitHub(_forge, "feature/retry", Acme, DeleteAnswer.RefusesProtected);

        var result = await MergeOnGitHubAsync(deleteSourceBranch: true);

        result.Merged.ShouldBeTrue("the merge landed; a refused cleanup must not turn it into a failed merge");
        result.SourceBranchDeletion.ShouldBe(SourceBranchDeletion.Failed);
        result.SourceBranchDetail.ShouldNotBeNull().ShouldContain("HTTP 422");
        result.SourceBranchDetail.ShouldContain("Cannot delete this protected branch", Case.Sensitive, "GitHub's own words say why");
        github.BranchExists.ShouldBeTrue();
    }

    [Theory]
    [InlineData(502)]
    [InlineData(403)]
    public async Task GitHub_reports_a_refused_delete_as_failed_when_the_branch_cannot_be_read_back(int branchReadStatus)
    {
        // Only a 404 shows that a refused delete left nothing behind. A read that fails says nothing about the branch, so
        // the refusal stands: taken as "gone", it would report Deleted for a branch that is still there.
        var github = new LoopbackGitHub(_forge, "feature/retry", Acme, DeleteAnswer.RefusesProtected) { BranchReadFailure = branchReadStatus };

        var result = await MergeOnGitHubAsync(deleteSourceBranch: true);

        result.Merged.ShouldBeTrue();
        result.SourceBranchDeletion.ShouldBe(SourceBranchDeletion.Failed, result.SourceBranchDetail);
        result.SourceBranchDetail.ShouldNotBeNull().ShouldContain($"HTTP {branchReadStatus}");
        result.SourceBranchDetail.ShouldNotContain("Deleted 'feature/retry'", Case.Sensitive);
        github.BranchExists.ShouldBeTrue();
    }

    [Theory]
    [InlineData(DeleteAnswer.DeletesThenConnectionDrops, false, "The merge stands, but deleting its source branch was cancelled before GitHub confirmed it.")]
    [InlineData(DeleteAnswer.RefusesProtected, true, "Cannot delete this protected branch")]
    public async Task GitHub_reports_a_cleanup_cancelled_mid_delete_and_the_merge_still_stands(DeleteAnswer answer, bool branchLeft, string expectedDetail)
    {
        // The caller cancels while the DELETE is in flight, after the merge landed. The cancel stops the cleanup (no
        // second DELETE) and what the cleanup knew is reported like any cleanup that could not finish: a throw here would
        // read as a failed merge. A delete whose answer was lost is not confirmed either way, so it is not called deleted.
        using var cancel = new CancellationTokenSource();
        var github = new LoopbackGitHub(_forge, "feature/retry", Acme, answer) { OnDelete = cancel.Cancel };

        var result = await MergeOnGitHubAsync(deleteSourceBranch: true, cancel.Token);

        result.Merged.ShouldBeTrue("the merge landed before the cancel");
        result.SourceBranchDeletion.ShouldBe(SourceBranchDeletion.Failed, result.SourceBranchDetail);
        result.SourceBranchDetail.ShouldNotBeNull().ShouldContain(expectedDetail);
        github.BranchExists.ShouldBe(branchLeft);
        _forge.Sent("DELETE", "/repos/acme/api/git/refs/heads/feature/retry").ShouldBe(1, "a cancelled cleanup does not try again");
    }

    [Fact]
    public async Task GitHub_reports_a_cleanup_that_cannot_read_the_pull_request_and_the_merge_still_stands()
    {
        _forge.Answer("PUT", "/repos/acme/api/pulls/7/merge", 200, MergedJson).Answer("GET", "/repos/acme/api/pulls/7", 503, """{"message":"Service Unavailable"}""");

        var result = await MergeOnGitHubAsync(deleteSourceBranch: true);

        result.Merged.ShouldBeTrue("the merge landed before the cleanup started");
        result.SourceBranchDeletion.ShouldBe(SourceBranchDeletion.Failed);
        result.SourceBranchDetail.ShouldNotBeNull().ShouldContain("HTTP 503");
        _forge.Requests.ShouldNotContain(r => r.Method == "DELETE");
    }

    [Fact]
    public async Task GitHub_reads_and_deletes_nothing_when_no_delete_was_asked_for()
    {
        var github = new LoopbackGitHub(_forge, "feature/retry", Acme, DeleteAnswer.Deletes);

        var result = await MergeOnGitHubAsync(deleteSourceBranch: false);

        result.Merged.ShouldBeTrue();
        result.SourceBranchDeletion.ShouldBe(SourceBranchDeletion.NotRequested);
        result.SourceBranchDetail.ShouldBeNull();
        github.BranchExists.ShouldBeTrue();
        _forge.Requests.ShouldHaveSingleItem().Method.ShouldBe("PUT", "the merge is the only call");
    }

    [Theory]
    [InlineData(true, SourceBranchDeletion.Requested)]
    [InlineData(false, SourceBranchDeletion.NotRequested)]
    public async Task GitLab_asks_for_the_source_branch_to_go_and_leaves_where_to_GitLab(bool deleteSourceBranch, SourceBranchDeletion expected)
    {
        // A fork's merge request (source project 9090, target 4242). GitLab removes the source branch from the merge
        // request's source project, never from the target, so the provider sends the flag and deletes nothing itself.
        // Sending false explicitly also keeps GitLab from falling back to the author's own "delete source branch" choice.
        _forge.Answer("PUT", "/api/v4/projects/4242/merge_requests/7/merge", 200, GitLabForkMergeRequestJson);

        var result = await GitLabProvider().MergePullRequestAsync(GitLabContext(), Repository, 7, new MergePullRequestInput { DeleteSourceBranch = deleteSourceBranch }, CancellationToken.None);

        result.Merged.ShouldBeTrue();
        result.SourceBranchDeletion.ShouldBe(expected);
        result.SourceBranchDetail.ShouldBe(deleteSourceBranch ? "Asked GitLab to delete 'release' from the merge request's own source project once the merge completes; GitLab does so when the merging identity may push there." : null);
        var accept = _forge.Requests.ShouldHaveSingleItem("the accept is the only call; no branch is deleted from the target project");
        JsonDocument.Parse(accept.Body).RootElement.GetProperty("should_remove_source_branch").GetBoolean().ShouldBe(deleteSourceBranch);
    }

    /// <summary>How the loopback GitHub answers the DELETE of the head branch's ref.</summary>
    public enum DeleteAnswer
    {
        /// <summary>Deletes the branch and answers 204.</summary>
        Deletes,

        /// <summary>Deletes the branch, then a gateway answers 502.</summary>
        DeletesThenGatewayError,

        /// <summary>Deletes the branch, then the connection drops mid-answer.</summary>
        DeletesThenConnectionDrops,

        /// <summary>The branch is already gone (the repository deletes head branches itself); GitHub answers 422.</summary>
        AlreadyGone,

        /// <summary>The branch is protected; GitHub refuses with 422 and the branch stays.</summary>
        RefusesProtected
    }

    // ── Loopback GitHub ──

    private static readonly object Acme = new { id = 4242, name = "api", full_name = "acme/api", owner = new { login = "acme" } };

    private static readonly object Outsider = new { id = 9090, name = "api", full_name = "outsider/api", owner = new { login = "outsider" }, fork = true };

    private const string MergeSha = "9f8e7d6c5b4a";

    private static readonly string MergedJson = JsonSerializer.Serialize(new { sha = MergeSha, merged = true, message = "Pull Request successfully merged" });

    private static readonly RemoteRepository Repository = new()
    {
        ExternalId = "4242",
        NamespacePath = "acme",
        Name = "api",
        FullPath = "acme/api",
        DefaultBranch = "main",
        Visibility = RepositoryVisibility.Private,
        WebUrl = "https://forge.test/acme/api"
    };

    private Task<RemotePullRequestMergeResult> MergeOnGitHubAsync(bool deleteSourceBranch, CancellationToken cancellationToken = default) =>
        GitHubProvider().MergePullRequestAsync(Context(ProviderKind.GitHub), Repository, 7, new MergePullRequestInput { Method = PullRequestMergeMethod.Squash, DeleteSourceBranch = deleteSourceBranch }, cancellationToken);

    private ProviderContext GitLabContext() => Context(ProviderKind.GitLab);

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

    /// <summary>
    /// acme/api, with pull request #7 from <c>headRef</c> in <c>headRepository</c> and a branch of that name in acme/api.
    /// The merge lands. A DELETE of the ref answers per <see cref="DeleteAnswer"/>; a branch read answers whether it is
    /// still there, or fails with <see cref="BranchReadFailure"/> when one is set.
    /// </summary>
    private sealed class LoopbackGitHub
    {
        private readonly string _headRef;
        private readonly object? _headRepository;
        private readonly DeleteAnswer _answer;

        public LoopbackGitHub(StubProviderHost host, string headRef, object? headRepository, DeleteAnswer answer)
        {
            _headRef = headRef;
            _headRepository = headRepository;
            _answer = answer;
            BranchExists = answer != DeleteAnswer.AlreadyGone;

            host.Answer("PUT", "/repos/acme/api/pulls/7/merge", 200, MergedJson).Answer("GET", "/repos/acme/api/pulls/7", _ => new StubReply(200, PullRequestJson())).Answer("DELETE", $"/repos/acme/api/git/refs/heads/{headRef}", _ => Delete()).Answer("GET", $"/repos/acme/api/branches/{headRef}", _ => ReadBranch());
        }

        public bool BranchExists { get; private set; }

        /// <summary>The status every branch read fails with, whatever the branch's state. Null: reads answer truthfully.</summary>
        public int? BranchReadFailure { get; init; }

        /// <summary>Runs when the DELETE arrives, before it is answered — where a caller's cancel lands mid-cleanup.</summary>
        public Action? OnDelete { get; init; }

        private StubReply Delete()
        {
            OnDelete?.Invoke();

            if (!BranchExists) return new StubReply(422, """{"message":"Reference does not exist"}""");
            if (_answer == DeleteAnswer.RefusesProtected) return new StubReply(422, """{"message":"Cannot delete this protected branch"}""");

            BranchExists = false;

            return _answer switch
            {
                DeleteAnswer.DeletesThenGatewayError => new StubReply(502, """{"message":"502 Bad Gateway"}"""),
                DeleteAnswer.DeletesThenConnectionDrops => StubReply.DropConnection,
                _ => new StubReply(204, string.Empty)
            };
        }

        private StubReply ReadBranch()
        {
            if (BranchReadFailure is { } status) return new StubReply(status, """{"message":"Branch read failed"}""");

            return BranchExists ? new StubReply(200, JsonSerializer.Serialize(new { name = _headRef, commit = new { sha = "0a1b2c3d" }, @protected = _answer == DeleteAnswer.RefusesProtected })) : new StubReply(404, """{"message":"Branch not found"}""");
        }

        private string PullRequestJson() => JsonSerializer.Serialize(new
        {
            id = 7007,
            number = 7,
            title = "Retry safely",
            state = "closed",
            merged = true,
            merged_at = "2026-09-24T08:00:00Z",
            merge_commit_sha = MergeSha,
            head = new { @ref = _headRef, sha = "0a1b2c3d", repo = _headRepository },
            @base = new { @ref = "main", sha = "4e5f6a7b", repo = Acme },
            user = new { login = "outsider" },
            html_url = "https://forge.test/acme/api/pull/7"
        });
    }

    // ── Loopback GitLab ──

    private static readonly string GitLabForkMergeRequestJson = JsonSerializer.Serialize(new
    {
        id = 7007,
        iid = 7,
        project_id = 4242,
        source_project_id = 9090,
        target_project_id = 4242,
        title = "Retry safely",
        state = "merged",
        merge_commit_sha = MergeSha,
        source_branch = "release",
        target_branch = "main",
        author = new { id = 2, username = "outsider", name = "Outsider" },
        created_at = "2026-09-24T08:00:00.000Z",
        updated_at = "2026-09-24T08:00:00.000Z",
        web_url = "https://forge.test/acme/api/-/merge_requests/7"
    });

    private sealed class StaticTokenAuth : IProviderAuthResolver
    {
        public Task<ResolvedAuth> ResolveAsync(ProviderContext context, CancellationToken cancellationToken) => Task.FromResult(new ResolvedAuth { Token = "fake-loopback-token" });
    }
}

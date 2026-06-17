using System.Net.Http;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Agents;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Exceptions;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;

namespace CodeSpace.UnitTests.PullRequests;

/// <summary>
/// <see cref="ChangeSetService"/> — the per-repo PR-open loop that converges a multi-repo Change Set. Driven against a
/// recording <see cref="IPullRequestService"/> stub: pins the per-repo team-scoped open, the failure-ISOLATION (one
/// repo's provider rejection never sinks the set), the no-source-branch skip, and the roll-up counts.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChangeSetServiceTests
{
    private static readonly Guid Web = Guid.NewGuid();
    private static readonly Guid Api = Guid.NewGuid();
    private static readonly Guid Team = Guid.NewGuid();

    [Fact]
    public async Task Opens_one_pr_per_repo_team_scoped_with_the_shared_title()
    {
        var stub = new RecordingPrService();

        var result = await Service(stub).OpenPullRequestsAsync(Team, Spec(
            (Web, "codespace/run-x", "main"),
            (Api, "codespace/run-x", "develop")), actorUserId: null, CancellationToken.None);

        result.OpenedCount.ShouldBe(2);
        result.FailedCount.ShouldBe(0);
        result.SkippedCount.ShouldBe(0);
        result.PullRequests.Count.ShouldBe(2);

        stub.Calls.Count.ShouldBe(2);
        stub.Calls.ShouldAllBe(c => c.TeamId == Team, "every repo is opened team-scoped (the fail-closed boundary)");
        stub.Calls.Single(c => c.RepoId == Web).Input.TargetBranch.ShouldBe("main");
        stub.Calls.Single(c => c.RepoId == Api).Input.TargetBranch.ShouldBe("develop");
        stub.Calls.ShouldAllBe(c => c.Input.Title == "Coordinated change");

        var web = result.PullRequests.Single(p => p.RepositoryId == Web);
        web.Disposition.ShouldBe(ChangeSetPullRequestDisposition.Opened);
        web.Number.ShouldBe(101);
        web.Url.ShouldBe("https://example.test/pr/101");
    }

    [Fact]
    public async Task One_repos_provider_failure_is_isolated_the_rest_still_open()
    {
        // The honesty invariant: api's 422 is recorded as Failed; web still opens. The set never aborts.
        var stub = new RecordingPrService { ThrowForRepo = { [Api] = new ProviderApiException(ProviderKind.GitHub, 422, "open", "Unprocessable", new Exception()) } };

        var result = await Service(stub).OpenPullRequestsAsync(Team, Spec(
            (Web, "b", "main"),
            (Api, "b", "main")), actorUserId: null, CancellationToken.None);

        result.OpenedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(1);
        result.PullRequests.Single(p => p.RepositoryId == Web).Disposition.ShouldBe(ChangeSetPullRequestDisposition.Opened);

        var api = result.PullRequests.Single(p => p.RepositoryId == Api);
        api.Disposition.ShouldBe(ChangeSetPullRequestDisposition.Failed);
        api.Error.ShouldNotBeNullOrEmpty();
        api.Number.ShouldBeNull();
    }

    [Fact]
    public async Task A_transient_infrastructure_failure_is_isolated_not_thrown()
    {
        // The honesty invariant must hold for a TRANSIENT failure too (a network blip that survived the resilience
        // layer's retries surfaces as a raw HttpRequestException, NOT a typed provider exception). web opens; api is
        // isolated as Failed — the set is never sunk by an un-typed exception.
        var stub = new RecordingPrService { ThrowForRepo = { [Api] = new HttpRequestException("connection reset") } };

        var result = await Service(stub).OpenPullRequestsAsync(Team, Spec(
            (Web, "b", "main"),
            (Api, "b", "main")), actorUserId: null, CancellationToken.None);

        result.OpenedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(1);
        result.PullRequests.Single(p => p.RepositoryId == Web).Disposition.ShouldBe(ChangeSetPullRequestDisposition.Opened);
        var api = result.PullRequests.Single(p => p.RepositoryId == Api);
        api.Disposition.ShouldBe(ChangeSetPullRequestDisposition.Failed);
        api.Error.ShouldNotContain("connection reset", customMessage: "an unknown exception's raw message must not leak into the per-repo error");
    }

    [Fact]
    public async Task A_non_caller_cancellation_is_isolated_like_any_transient_error()
    {
        // An OperationCanceledException whose token is NOT the caller's (e.g. an SDK read timeout) is a transient
        // failure, not a run cancellation — it must be isolated, not abort the set.
        var stub = new RecordingPrService { ThrowForRepo = { [Api] = new OperationCanceledException() } };

        var result = await Service(stub).OpenPullRequestsAsync(Team, Spec(
            (Web, "b", "main"),
            (Api, "b", "main")), actorUserId: null, CancellationToken.None);

        result.OpenedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(1);
        result.PullRequests.Single(p => p.RepositoryId == Api).Disposition.ShouldBe(ChangeSetPullRequestDisposition.Failed);
    }

    [Fact]
    public async Task A_genuine_caller_cancellation_propagates()
    {
        // When the run's own token is signalled (operator kill / run timeout) the whole set aborts — it is NOT
        // swallowed into a Failed disposition.
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var stub = new RecordingPrService();

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Service(stub).OpenPullRequestsAsync(Team, Spec((Web, "b", "main")), actorUserId: null, cts.Token));
    }

    [Fact]
    public async Task A_repo_with_no_source_branch_is_skipped_not_opened()
    {
        var stub = new RecordingPrService();

        var result = await Service(stub).OpenPullRequestsAsync(Team, Spec(
            (Web, "codespace/run-x", "main"),
            (Api, "", "main")), actorUserId: null, CancellationToken.None);   // api produced no branch

        result.OpenedCount.ShouldBe(1);
        result.SkippedCount.ShouldBe(1);
        stub.Calls.Count.ShouldBe(1, "the no-branch repo never reaches the provider");
        result.PullRequests.Single(p => p.RepositoryId == Api).Disposition.ShouldBe(ChangeSetPullRequestDisposition.Skipped);
    }

    [Fact]
    public async Task A_repo_with_a_head_branch_but_no_base_is_failed_not_skipped()
    {
        // The base couldn't be resolved (no ref recorded for this repo) — it has work to land but no PR target, so it's
        // a per-repo FAILURE (distinct from the no-changes Skip), and it must never reach the provider as an empty target.
        var stub = new RecordingPrService();

        var result = await Service(stub).OpenPullRequestsAsync(Team, Spec(
            (Web, "codespace/run-x", "main"),
            (Api, "codespace/run-x", "")), actorUserId: null, CancellationToken.None);   // api: head but no base

        result.OpenedCount.ShouldBe(1);
        result.FailedCount.ShouldBe(1);
        result.SkippedCount.ShouldBe(0);
        stub.Calls.Count.ShouldBe(1, "the no-base repo never reaches the provider");

        var api = result.PullRequests.Single(p => p.RepositoryId == Api);
        api.Disposition.ShouldBe(ChangeSetPullRequestDisposition.Failed);
        api.Error.ShouldContain("base");
    }

    [Fact]
    public async Task Threads_the_actor_user_through_to_each_open()
    {
        var stub = new RecordingPrService();
        var actor = Guid.NewGuid();

        await Service(stub).OpenPullRequestsAsync(Team, Spec((Web, "b", "main")), actorUserId: actor, CancellationToken.None);

        stub.Calls.Single().ActorUserId.ShouldBe(actor);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static ChangeSetService Service(IPullRequestService prs) => new(prs, NullLogger<ChangeSetService>.Instance);

    private static ChangeSetPullRequestSpec Spec(params (Guid repo, string source, string target)[] repos) => new()
    {
        Title = "Coordinated change",
        Repositories = repos.Select(r => new ChangeSetPullRequest { RepositoryId = r.repo, SourceBranch = r.source, TargetBranch = r.target }).ToList(),
    };

    private sealed record Call(Guid RepoId, Guid TeamId, OpenPullRequestInput Input, Guid? ActorUserId);

    private sealed class RecordingPrService : IPullRequestService
    {
        public List<Call> Calls { get; } = new();
        public Dictionary<Guid, Exception> ThrowForRepo { get; } = new();
        private int _next = 100;

        public Task<RemotePullRequest> OpenPullRequestAsync(Guid repositoryId, Guid teamId, OpenPullRequestInput input, Guid? actorUserId, CancellationToken cancellationToken)
        {
            Calls.Add(new Call(repositoryId, teamId, input, actorUserId));
            if (ThrowForRepo.TryGetValue(repositoryId, out var ex)) throw ex;

            var number = ++_next;
            return Task.FromResult(new RemotePullRequest
            {
                ExternalId = $"pr-{number}", Number = number, Title = input.Title, State = PullRequestState.Open,
                SourceBranch = input.SourceBranch, TargetBranch = input.TargetBranch, CommentsCount = 0,
                CreatedDate = DateTimeOffset.UnixEpoch, UpdatedDate = DateTimeOffset.UnixEpoch, WebUrl = $"https://example.test/pr/{number}",
            });
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid r, Guid t, PullRequestState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, Guid t, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid r, Guid t, int n, PullRequestReviewVerdict v, string? b, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid r, Guid t, int n, MergePullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }
}

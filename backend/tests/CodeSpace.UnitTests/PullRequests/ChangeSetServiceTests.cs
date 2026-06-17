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

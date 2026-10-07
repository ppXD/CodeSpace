using CodeSpace.Core.Handlers.QueryHandlers.Repositories;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Repositories;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Repositories;

[Trait("Category", "Unit")]
public class ListPullRequestsQueryHandlerTests
{
    [Fact]
    public void The_page_ceilings_are_pinned()
    {
        // The node an agent calls and the Pulls tab share these. Raising either one widens what one request can ask a
        // provider for, so it is a reviewed edit, not a drift.
        ListPullRequestsQuery.MaxPerPage.ShouldBe(100);
        ListPullRequestsQuery.MaxPage.ShouldBe(1000);
    }

    [Theory]
    [InlineData(0, 30, 1, 30)]                          // page < 1 clamps up to 1
    [InlineData(3, 30, 3, 30)]                          // in-range values pass through
    [InlineData(1, 0, 1, 1)]                            // perPage < 1 clamps up to 1
    [InlineData(1, int.MaxValue, 1, 100)]               // perPage past the providers' cap is their cap
    [InlineData(1_000_000, 100, 1000, 100)]             // a page past the ceiling is the ceiling
    [InlineData(int.MaxValue, int.MaxValue, 1000, 100)]
    public async Task Clamps_page_and_perPage_then_forwards_with_the_current_team(int page, int perPage, int expectedPage, int expectedPerPage)
    {
        var team = Guid.NewGuid();
        var repo = Guid.NewGuid();
        var service = new StubPullRequestService();

        await new ListPullRequestsQueryHandler(service, new StubCurrentTeam(team)).Handle(new ListPullRequestsQuery { RepositoryId = repo, State = PullRequestState.Open, Page = page, PerPage = perPage }, CancellationToken.None);

        service.Calls.ShouldBe(1);
        service.RepoId.ShouldBe(repo);
        service.TeamId.ShouldBe(team);
        service.State.ShouldBe(PullRequestState.Open);
        service.Page.ShouldBe(expectedPage);
        service.PerPage.ShouldBe(expectedPerPage);
    }

    /// <summary>Records the list call; every other member throws (the handler only lists).</summary>
    private sealed class StubPullRequestService : IPullRequestService
    {
        public Guid RepoId;
        public Guid TeamId;
        public PullRequestState? State;
        public int Page;
        public int PerPage;
        public int Calls;

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid repositoryId, Guid teamId, PullRequestState? state, int page, int perPage, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; TeamId = teamId; State = state; Page = page; PerPage = perPage; Calls++;
            return Task.FromResult((IReadOnlyList<RemotePullRequest>)Array.Empty<RemotePullRequest>());
        }

        public Task<RemotePullRequest> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, Guid t, int n, string b, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid r, Guid t, int n, SubmitPullRequestReviewInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> OpenPullRequestAsync(Guid r, Guid t, OpenPullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestMergeResult> MergePullRequestAsync(Guid r, Guid t, int n, MergePullRequestInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    private sealed class StubCurrentTeam(Guid? id) : ICurrentTeam
    {
        public Guid? Id => id;
        public bool IsSet => Id is not null;
    }
}

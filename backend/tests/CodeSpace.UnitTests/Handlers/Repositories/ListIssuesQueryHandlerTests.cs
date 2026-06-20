using CodeSpace.Core.Handlers.QueryHandlers.Repositories;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Issues;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Repositories;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Repositories;

[Trait("Category", "Unit")]
public class ListIssuesQueryHandlerTests
{
    [Theory]
    [InlineData(0, 30, 1, 30)]                 // page < 1 clamps up to 1
    [InlineData(-5, 30, 1, 30)]
    [InlineData(3, 30, 3, 30)]                 // in-range values pass through
    [InlineData(1, 0, 1, 1)]                   // perPage < 1 clamps up to 1
    [InlineData(1, 1000, 1, 100)]             // perPage > Max clamps down to MaxPerPage
    public async Task Clamps_page_and_perPage_then_forwards_with_the_current_team(int page, int perPage, int expectedPage, int expectedPerPage)
    {
        var team = Guid.NewGuid();
        var repo = Guid.NewGuid();
        var service = new StubIssueService();
        var handler = new ListIssuesQueryHandler(service, new StubCurrentTeam(team));

        await handler.Handle(new ListIssuesQuery { RepositoryId = repo, State = IssueState.Open, Page = page, PerPage = perPage }, CancellationToken.None);

        service.Calls.ShouldBe(1);
        service.RepoId.ShouldBe(repo);
        service.TeamId.ShouldBe(team, "the handler must scope the read to the current team so the repo load is fail-closed");
        service.State.ShouldBe(IssueState.Open);
        service.Page.ShouldBe(expectedPage);
        service.PerPage.ShouldBe(expectedPerPage);
    }

    /// <summary>Hand-rolled stub (no mocking lib) — records the list call; writes/counts throw.</summary>
    private sealed class StubIssueService : IIssueService
    {
        public Guid RepoId;
        public Guid TeamId;
        public IssueState? State;
        public int Page;
        public int PerPage;
        public int Calls;

        public Task<IReadOnlyList<RemoteIssue>> ListAsync(Guid repositoryId, Guid teamId, IssueState? state, int page, int perPage, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; TeamId = teamId; State = state; Page = page; PerPage = perPage; Calls++;
            return Task.FromResult((IReadOnlyList<RemoteIssue>)Array.Empty<RemoteIssue>());
        }

        public Task<RemoteIssueCounts> GetCountsAsync(Guid r, Guid t, CancellationToken c) => throw new NotImplementedException();
        public Task<RemoteIssue> GetAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemoteIssueComment>> ListCommentsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemoteIssueEvent>> ListEventsAsync(Guid r, Guid t, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemoteIssue> CreateAsync(Guid r, Guid t, CreateIssueInput i, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemoteIssueComment> CommentAsync(Guid r, Guid t, int n, string b, Guid? a, CancellationToken c) => throw new NotImplementedException();
        public Task<RemoteIssue> CloseAsync(Guid r, Guid t, int n, Guid? a, CancellationToken c) => throw new NotImplementedException();
    }

    /// <summary>Minimal ICurrentTeam double — only Id varies; the handler reads nothing else.</summary>
    private sealed class StubCurrentTeam : ICurrentTeam
    {
        public StubCurrentTeam(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public bool IsSet => Id is not null;
    }
}

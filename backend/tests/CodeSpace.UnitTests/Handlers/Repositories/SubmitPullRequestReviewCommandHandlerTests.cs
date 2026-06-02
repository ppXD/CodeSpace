using CodeSpace.Core.Handlers.CommandHandlers.Repositories;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Enums;
using Shouldly;

namespace CodeSpace.UnitTests.Handlers.Repositories;

[Trait("Category", "Unit")]
public class SubmitPullRequestReviewCommandHandlerTests
{
    [Fact]
    public async Task Forwards_the_route_args_and_the_callers_id_as_the_actor()
    {
        var caller = Guid.NewGuid();
        var repo = Guid.NewGuid();
        var service = new StubPrService();
        var handler = new SubmitPullRequestReviewCommandHandler(service, new StubCurrentUser(caller));

        var result = await handler.Handle(
            new SubmitPullRequestReviewCommand { RepositoryId = repo, Number = 7, Verdict = PullRequestReviewVerdict.RequestChanges, Body = "please fix" },
            CancellationToken.None);

        service.Calls.ShouldBe(1);
        service.RepoId.ShouldBe(repo);
        service.Number.ShouldBe(7);
        service.Verdict.ShouldBe(PullRequestReviewVerdict.RequestChanges);
        service.Body.ShouldBe("please fix");
        service.ActorUserId.ShouldBe(caller, "the handler must act AS the caller, never fall back to the connection credential");
        result.Verdict.ShouldBe(PullRequestReviewVerdict.RequestChanges);
    }

    [Fact]
    public async Task Rejects_an_anonymous_caller_without_touching_the_service()
    {
        var service = new StubPrService();
        var handler = new SubmitPullRequestReviewCommandHandler(service, new StubCurrentUser(null));

        await Should.ThrowAsync<UnauthorizedAccessException>(() => handler.Handle(
            new SubmitPullRequestReviewCommand { RepositoryId = Guid.NewGuid(), Number = 1, Verdict = PullRequestReviewVerdict.Approve },
            CancellationToken.None));

        service.Calls.ShouldBe(0, "an anonymous caller has no identity to act as — the review must never reach the provider");
    }

    /// <summary>Hand-rolled stub (no mocking lib) — records the review call, returns a canned result; reads throw.</summary>
    private sealed class StubPrService : IPullRequestService
    {
        public Guid RepoId;
        public int Number;
        public PullRequestReviewVerdict Verdict;
        public string? Body;
        public Guid? ActorUserId;
        public int Calls;

        public Task<RemotePullRequestReview> SubmitReviewAsync(Guid repositoryId, int number, PullRequestReviewVerdict verdict, string? body, Guid? actorUserId, CancellationToken cancellationToken)
        {
            RepoId = repositoryId; Number = number; Verdict = verdict; Body = body; ActorUserId = actorUserId; Calls++;
            return Task.FromResult(new RemotePullRequestReview { Verdict = verdict, ExternalId = "rev-1", WebUrl = "https://example.test/review/1" });
        }

        public Task<IReadOnlyList<RemotePullRequest>> ListAsync(Guid r, PullRequestState? s, int p, int pp, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequest> GetAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCommit>> ListCommitsAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestFile>> ListFilesAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestCounts> GetCountsAsync(Guid r, CancellationToken c) => throw new NotImplementedException();
        public Task<IReadOnlyList<RemotePullRequestCheck>> ListChecksAsync(Guid r, int n, CancellationToken c) => throw new NotImplementedException();
        public Task<RemotePullRequestComment> PostCommentAsync(Guid r, int n, string b, CancellationToken c) => throw new NotImplementedException();
    }

    /// <summary>Minimal ICurrentUser double — only Id varies; the handler reads nothing else.</summary>
    private sealed class StubCurrentUser : ICurrentUser
    {
        public StubCurrentUser(Guid? id) { Id = id; }

        public Guid? Id { get; }
        public string Name => "tester";
        public IReadOnlyList<string> Roles => Array.Empty<string>();
        public IReadOnlyList<string> Permissions => Array.Empty<string>();
        public bool HasRole(string role) => false;
        public bool HasPermission(string permission) => false;
        public bool PasswordMustChange => false;
    }
}

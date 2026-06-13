using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.PullRequests;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

public sealed class SubmitPullRequestReviewCommandHandler : IRequestHandler<SubmitPullRequestReviewCommand, RemotePullRequestReview>
{
    private readonly IPullRequestService _service;
    private readonly ICurrentUser _currentUser;
    private readonly ICurrentTeam _currentTeam;

    public SubmitPullRequestReviewCommandHandler(IPullRequestService service, ICurrentUser currentUser, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentUser = currentUser;
        _currentTeam = currentTeam;
    }

    public async Task<RemotePullRequestReview> Handle(SubmitPullRequestReviewCommand request, CancellationToken cancellationToken)
    {
        // Act-as-user is the whole point of this endpoint: the review must be attributed to the
        // caller's own identity, never the repo's connection credential. An anonymous caller has
        // no identity to act as — reject rather than silently fall back.
        var actorUserId = _currentUser.Id ?? throw new UnauthorizedAccessException("A pull request review can only be submitted by an authenticated user.");

        return await _service.SubmitReviewAsync(request.RepositoryId, _currentTeam.Id!.Value, request.Number, request.Verdict, request.Body, actorUserId, cancellationToken).ConfigureAwait(false);
    }
}

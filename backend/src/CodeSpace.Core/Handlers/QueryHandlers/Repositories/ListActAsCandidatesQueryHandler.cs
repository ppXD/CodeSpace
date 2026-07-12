using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListActAsCandidatesQueryHandler : IRequestHandler<ListActAsCandidatesQuery, IReadOnlyList<ActAsCandidateSummary>>
{
    private readonly IActorIdentityResolver _resolver;
    private readonly ICurrentTeam _currentTeam;

    public ListActAsCandidatesQueryHandler(IActorIdentityResolver resolver, ICurrentTeam currentTeam) { _resolver = resolver; _currentTeam = currentTeam; }

    public Task<IReadOnlyList<ActAsCandidateSummary>> Handle(ListActAsCandidatesQuery request, CancellationToken cancellationToken) =>
        _resolver.ListCandidatesAsync(request.RepositoryId, _currentTeam.Id!.Value, cancellationToken);
}

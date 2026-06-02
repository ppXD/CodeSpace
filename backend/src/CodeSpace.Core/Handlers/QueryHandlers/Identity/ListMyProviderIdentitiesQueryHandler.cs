using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Identity;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Identity;

public sealed class ListMyProviderIdentitiesQueryHandler : IRequestHandler<ListMyProviderIdentitiesQuery, IReadOnlyList<UserProviderIdentitySummary>>
{
    private readonly IUserProviderIdentityService _service;

    public ListMyProviderIdentitiesQueryHandler(IUserProviderIdentityService service) { _service = service; }

    public Task<IReadOnlyList<UserProviderIdentitySummary>> Handle(ListMyProviderIdentitiesQuery request, CancellationToken cancellationToken) =>
        _service.ListMineAsync(cancellationToken);
}

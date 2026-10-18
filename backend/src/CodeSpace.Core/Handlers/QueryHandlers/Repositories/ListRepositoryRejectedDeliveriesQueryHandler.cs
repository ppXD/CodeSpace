using CodeSpace.Core.Services.Credentials.Remedy;
using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListRepositoryRejectedDeliveriesQueryHandler : IRequestHandler<ListRepositoryRejectedDeliveriesQuery, RepositoryRejectedDeliveries>
{
    private readonly IRejectedDeliveryReader _reader;
    private readonly IProviderRemedyService _remedies;
    private readonly ICurrentTeam _currentTeam;

    public ListRepositoryRejectedDeliveriesQueryHandler(IRejectedDeliveryReader reader, IProviderRemedyService remedies, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _remedies = remedies;
        _currentTeam = currentTeam;
    }

    public async Task<RepositoryRejectedDeliveries> Handle(ListRepositoryRejectedDeliveriesQuery request, CancellationToken cancellationToken)
    {
        var refused = await _reader.ListForRepositoryAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);

        // The refusals that blame the repository's credential carry what to do about it, for whoever is asking.
        return refused with { Remedy = await _remedies.GetForRefusedDeliveriesAsync(request.RepositoryId, refused.Deliveries, _currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false) };
    }
}

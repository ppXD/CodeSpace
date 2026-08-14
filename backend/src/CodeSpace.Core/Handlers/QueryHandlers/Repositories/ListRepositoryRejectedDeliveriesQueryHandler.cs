using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListRepositoryRejectedDeliveriesQueryHandler : IRequestHandler<ListRepositoryRejectedDeliveriesQuery, RepositoryRejectedDeliveries>
{
    private readonly IRejectedDeliveryReader _reader;

    public ListRepositoryRejectedDeliveriesQueryHandler(IRejectedDeliveryReader reader) { _reader = reader; }

    public async Task<RepositoryRejectedDeliveries> Handle(ListRepositoryRejectedDeliveriesQuery request, CancellationToken cancellationToken) =>
        await _reader.ListForRepositoryAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
}

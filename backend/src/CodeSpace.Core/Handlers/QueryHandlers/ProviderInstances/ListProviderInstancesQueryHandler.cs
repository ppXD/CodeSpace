using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Dtos.ProviderInstances;
using CodeSpace.Messages.Queries.ProviderInstances;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.ProviderInstances;

public sealed class ListProviderInstancesQueryHandler : IRequestHandler<ListProviderInstancesQuery, IReadOnlyList<ProviderInstanceSummary>>
{
    private readonly IProviderInstanceService _service;

    public ListProviderInstancesQueryHandler(IProviderInstanceService service) { _service = service; }

    public async Task<IReadOnlyList<ProviderInstanceSummary>> Handle(ListProviderInstancesQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(cancellationToken).ConfigureAwait(false);
}

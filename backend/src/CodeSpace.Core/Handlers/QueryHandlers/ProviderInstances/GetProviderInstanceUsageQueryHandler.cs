using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Queries.ProviderInstances;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.ProviderInstances;

public sealed class GetProviderInstanceUsageQueryHandler : IRequestHandler<GetProviderInstanceUsageQuery, ProviderInstanceUsage>
{
    private readonly IProviderInstanceService _service;

    public GetProviderInstanceUsageQueryHandler(IProviderInstanceService service) { _service = service; }

    public async Task<ProviderInstanceUsage> Handle(GetProviderInstanceUsageQuery request, CancellationToken cancellationToken) =>
        await _service.GetUsageAsync(request.ProviderInstanceId, cancellationToken).ConfigureAwait(false);
}

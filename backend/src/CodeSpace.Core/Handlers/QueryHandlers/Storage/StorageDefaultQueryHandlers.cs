using CodeSpace.Core.Services.Workflows.Artifacts.Defaults;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

/// <summary>Deployment-template reads. Team-less: the answer is the same for every caller holding the instance capability.</summary>
public sealed class ListStorageDefaultsQueryHandler : IRequestHandler<ListStorageDefaultsQuery, IReadOnlyList<StorageDefaultSummary>>
{
    private readonly IStorageDefaultService _service;

    public ListStorageDefaultsQueryHandler(IStorageDefaultService service) { _service = service; }

    public async Task<IReadOnlyList<StorageDefaultSummary>> Handle(ListStorageDefaultsQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(cancellationToken).ConfigureAwait(false);
}

public sealed class GetStorageDefaultQueryHandler : IRequestHandler<GetStorageDefaultQuery, StorageDefaultDetail?>
{
    private readonly IStorageDefaultService _service;

    public GetStorageDefaultQueryHandler(IStorageDefaultService service) { _service = service; }

    public async Task<StorageDefaultDetail?> Handle(GetStorageDefaultQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(request.DefaultId, cancellationToken).ConfigureAwait(false);
}

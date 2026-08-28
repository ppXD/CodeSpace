using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Defaults;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

public sealed class ListStorageAdoptionsQueryHandler : IRequestHandler<ListStorageAdoptionsQuery, IReadOnlyList<StorageAdoptionStatus>>
{
    private readonly IStorageAdoptionReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public ListStorageAdoptionsQueryHandler(IStorageAdoptionReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<StorageAdoptionStatus>> Handle(ListStorageAdoptionsQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(_currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}

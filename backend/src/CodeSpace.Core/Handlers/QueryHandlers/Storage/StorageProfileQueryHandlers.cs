using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Dtos.Storage;
using CodeSpace.Messages.Queries.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Storage;

public sealed class ListStorageProfilesQueryHandler : IRequestHandler<ListStorageProfilesQuery, IReadOnlyList<StorageProfileSummary>>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListStorageProfilesQueryHandler(IStorageProfileService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<StorageProfileSummary>> Handle(ListStorageProfilesQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(_currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}

public sealed class ListStorageProfilePageQueryHandler : IRequestHandler<ListStorageProfilePageQuery, StoragePage<StorageProfileSummary>>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;

    public ListStorageProfilePageQueryHandler(IStorageProfileService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<StoragePage<StorageProfileSummary>> Handle(ListStorageProfilePageQuery request, CancellationToken cancellationToken) =>
        await _service.ListPageAsync(_currentTeam.Id!.Value, request.Cursor, request.Limit, cancellationToken).ConfigureAwait(false);
}

public sealed class GetStorageProfileQueryHandler : IRequestHandler<GetStorageProfileQuery, StorageProfileDetail?>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;

    public GetStorageProfileQueryHandler(IStorageProfileService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<StorageProfileDetail?> Handle(GetStorageProfileQuery request, CancellationToken cancellationToken) =>
        await _service.GetAsync(_currentTeam.Id!.Value, request.ProfileId, cancellationToken).ConfigureAwait(false);
}

public sealed class GetPlacementIntegrityQueryHandler : IRequestHandler<GetPlacementIntegrityQuery, PlacementIntegritySummary>
{
    private readonly IPlacementIntegrityReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetPlacementIntegrityQueryHandler(IPlacementIntegrityReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<PlacementIntegritySummary> Handle(GetPlacementIntegrityQuery request, CancellationToken cancellationToken) =>
        await _reader.ReadAsync(_currentTeam.Id!.Value, cancellationToken).ConfigureAwait(false);
}

public sealed class ListProfilePlacementsQueryHandler : IRequestHandler<ListProfilePlacementsQuery, ProfilePlacementPage>
{
    private readonly IProfilePlacementReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public ListProfilePlacementsQueryHandler(IProfilePlacementReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<ProfilePlacementPage> Handle(ListProfilePlacementsQuery request, CancellationToken cancellationToken) =>
        await _reader.ListAsync(_currentTeam.Id!.Value, request.ProfileId, request.Cursor, request.Limit, cancellationToken).ConfigureAwait(false);
}

public sealed class GetProfilePlacementTotalsQueryHandler : IRequestHandler<GetProfilePlacementTotalsQuery, IReadOnlyList<ProfilePlacementTotal>>
{
    private readonly IProfilePlacementReader _reader;
    private readonly ICurrentTeam _currentTeam;

    public GetProfilePlacementTotalsQueryHandler(IProfilePlacementReader reader, ICurrentTeam currentTeam)
    {
        _reader = reader;
        _currentTeam = currentTeam;
    }

    public async Task<IReadOnlyList<ProfilePlacementTotal>> Handle(GetProfilePlacementTotalsQuery request, CancellationToken cancellationToken) =>
        await _reader.TotalsAsync(_currentTeam.Id!.Value, request.ProfileId, cancellationToken).ConfigureAwait(false);
}

public sealed class GetLegacyPlacementSurveyQueryHandler : IRequestHandler<GetLegacyPlacementSurveyQuery, LegacyPlacementSurvey>
{
    private readonly ILegacyPlacementSurveyor _surveyor;
    private readonly ICurrentTeam _currentTeam;

    public GetLegacyPlacementSurveyQueryHandler(ILegacyPlacementSurveyor surveyor, ICurrentTeam currentTeam)
    {
        _surveyor = surveyor;
        _currentTeam = currentTeam;
    }

    public async Task<LegacyPlacementSurvey> Handle(GetLegacyPlacementSurveyQuery request, CancellationToken cancellationToken) =>
        await _surveyor.SurveyAsync(_currentTeam.Id!.Value, request.ProfileId, request.Limit, cancellationToken).ConfigureAwait(false);
}

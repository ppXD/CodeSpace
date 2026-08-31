using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.Workflows.Artifacts.Profiles;
using CodeSpace.Core.Services.Workflows.Artifacts.Runtime;
using CodeSpace.Messages.Commands.Storage;
using CodeSpace.Messages.Dtos.Storage;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Storage;

public sealed class CreateStorageProfileCommandHandler : IRequestHandler<CreateStorageProfileCommand, StorageProfileDetail>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public CreateStorageProfileCommandHandler(IStorageProfileService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageProfileDetail> Handle(CreateStorageProfileCommand request, CancellationToken cancellationToken) =>
        await _service.CreateAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class AppendStorageProfileRevisionCommandHandler : IRequestHandler<AppendStorageProfileRevisionCommand, StorageProfileDetail?>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AppendStorageProfileRevisionCommandHandler(IStorageProfileService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageProfileDetail?> Handle(AppendStorageProfileRevisionCommand request, CancellationToken cancellationToken) =>
        await _service.AppendRevisionAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class SetStorageProfileStateCommandHandler : IRequestHandler<SetStorageProfileStateCommand, StorageProfileDetail?>
{
    private readonly IStorageProfileService _service;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public SetStorageProfileStateCommandHandler(IStorageProfileService service, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _service = service;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<StorageProfileDetail?> Handle(SetStorageProfileStateCommand request, CancellationToken cancellationToken) =>
        await _service.SetStateAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request, cancellationToken).ConfigureAwait(false);
}

public sealed class AbandonProfilePlacementsCommandHandler : IRequestHandler<AbandonProfilePlacementsCommand, ProfileAbandonmentSummary>
{
    private readonly IProfileAbandonmentService _abandonment;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AbandonProfilePlacementsCommandHandler(IProfileAbandonmentService abandonment, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _abandonment = abandonment;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<ProfileAbandonmentSummary> Handle(AbandonProfilePlacementsCommand request, CancellationToken cancellationToken) =>
        await _abandonment.AbandonAsync(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.ProfileId, request.BatchSize, cancellationToken).ConfigureAwait(false);
}

public sealed class AdoptLegacyPlacementsCommandHandler : IRequestHandler<AdoptLegacyPlacementsCommand, LegacyPlacementAdoptionSummary>
{
    private readonly ILegacyPlacementAdopter _adopter;
    private readonly ICurrentTeam _currentTeam;
    private readonly ICurrentUser _currentUser;

    public AdoptLegacyPlacementsCommandHandler(ILegacyPlacementAdopter adopter, ICurrentTeam currentTeam, ICurrentUser currentUser)
    {
        _adopter = adopter;
        _currentTeam = currentTeam;
        _currentUser = currentUser;
    }

    public async Task<LegacyPlacementAdoptionSummary> Handle(AdoptLegacyPlacementsCommand request, CancellationToken cancellationToken) =>
        await _adopter.AdoptAsync(new LegacyPlacementAdoptionRequest(_currentTeam.Id!.Value, _currentUser.Id!.Value, request.ProfileId,
            request.BatchSize, request.Cursor), cancellationToken).ConfigureAwait(false);
}

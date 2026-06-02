using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Messages.Commands.Identity;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Identity;

public sealed class UnlinkProviderIdentityCommandHandler : IRequestHandler<UnlinkProviderIdentityCommand>
{
    private readonly IUserProviderIdentityService _service;

    public UnlinkProviderIdentityCommandHandler(IUserProviderIdentityService service) { _service = service; }

    public Task Handle(UnlinkProviderIdentityCommand request, CancellationToken cancellationToken) =>
        _service.UnlinkAsync(request.IdentityId, cancellationToken);
}

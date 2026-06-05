using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Messages.Commands.Identity;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Identity;

public sealed class UnlinkProviderIdentityCommandHandler : IRequestHandler<UnlinkProviderIdentityCommand, Unit>
{
    private readonly IUserProviderIdentityService _service;

    public UnlinkProviderIdentityCommandHandler(IUserProviderIdentityService service) { _service = service; }

    public async Task<Unit> Handle(UnlinkProviderIdentityCommand request, CancellationToken cancellationToken)
    {
        await _service.UnlinkAsync(request.IdentityId, cancellationToken);
        return Unit.Value;
    }
}

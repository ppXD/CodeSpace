using CodeSpace.Core.Services.Providers.Identity;
using CodeSpace.Messages.Commands.Identity;
using CodeSpace.Messages.Dtos.Providers;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Identity;

public sealed class LinkProviderIdentityByPatCommandHandler : IRequestHandler<LinkProviderIdentityByPatCommand, UserProviderIdentitySummary>
{
    private readonly IUserProviderIdentityService _service;

    public LinkProviderIdentityByPatCommandHandler(IUserProviderIdentityService service) { _service = service; }

    public Task<UserProviderIdentitySummary> Handle(LinkProviderIdentityByPatCommand request, CancellationToken cancellationToken) =>
        _service.LinkByPatAsync(request.ProviderInstanceId, request.AccessToken, cancellationToken);
}

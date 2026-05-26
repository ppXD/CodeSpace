using CodeSpace.Core.Services.ProviderInstances;
using CodeSpace.Messages.Commands.ProviderInstances;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ProviderInstances;

public sealed class AddProviderInstanceCommandHandler : IRequestHandler<AddProviderInstanceCommand, Guid>
{
    private readonly IProviderInstanceService _service;

    public AddProviderInstanceCommandHandler(IProviderInstanceService service) { _service = service; }

    public async Task<Guid> Handle(AddProviderInstanceCommand request, CancellationToken cancellationToken) =>
        await _service.AddAsync(request.Provider, request.DisplayName, request.BaseUrl, request.ApiUrl, request.WebUrl, request.OauthClientId, request.OauthClientSecret, request.OauthRedirectPath, request.OauthDefaultScopes, cancellationToken).ConfigureAwait(false);
}

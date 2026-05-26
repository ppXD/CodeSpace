using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class AddCredentialCommandHandler : IRequestHandler<AddCredentialCommand, Guid>
{
    private readonly ICredentialService _service;

    public AddCredentialCommandHandler(ICredentialService service) { _service = service; }

    public async Task<Guid> Handle(AddCredentialCommand request, CancellationToken cancellationToken) =>
        await _service.AddAsync(request.ProviderInstanceId, request.OwnerUserId, request.DisplayName, request.Payload, cancellationToken).ConfigureAwait(false);
}

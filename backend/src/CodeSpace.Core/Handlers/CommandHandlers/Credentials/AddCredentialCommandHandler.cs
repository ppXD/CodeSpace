using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Commands.Credentials;
using CodeSpace.Messages.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Credentials;

public sealed class AddCredentialCommandHandler : IRequestHandler<AddCredentialCommand, Guid>
{
    private readonly ICredentialService _service;

    public AddCredentialCommandHandler(ICredentialService service) { _service = service; }

    public async Task<Guid> Handle(AddCredentialCommand request, CancellationToken cancellationToken) =>
        await _service.AddAsync(new AddCredentialInput
        {
            ProviderInstanceId = request.ProviderInstanceId,
            OwnerUserId = request.OwnerUserId,
            DisplayName = request.DisplayName,
            Payload = request.Payload,
            Ownership = request.Ownership,
        }, cancellationToken).ConfigureAwait(false);
}

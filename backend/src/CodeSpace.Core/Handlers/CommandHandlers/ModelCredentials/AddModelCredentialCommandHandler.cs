using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class AddModelCredentialCommandHandler : IRequestHandler<AddModelCredentialCommand, Guid>
{
    private readonly IModelCredentialService _service;

    public AddModelCredentialCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<Guid> Handle(AddModelCredentialCommand request, CancellationToken cancellationToken) =>
        await _service.AddAsync(request.Provider, request.DisplayName, request.ApiKey, request.BaseUrl, cancellationToken).ConfigureAwait(false);
}

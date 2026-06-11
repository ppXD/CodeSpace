using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class UpdateModelCredentialCommandHandler : IRequestHandler<UpdateModelCredentialCommand, Guid>
{
    private readonly IModelCredentialService _service;

    public UpdateModelCredentialCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<Guid> Handle(UpdateModelCredentialCommand request, CancellationToken cancellationToken) =>
        await _service.UpdateAsync(request.Id, request.DisplayName, request.ApiKey, request.BaseUrl, cancellationToken).ConfigureAwait(false);
}

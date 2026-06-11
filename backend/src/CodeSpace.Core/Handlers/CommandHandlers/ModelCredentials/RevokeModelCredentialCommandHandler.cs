using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Commands.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.ModelCredentials;

public sealed class RevokeModelCredentialCommandHandler : IRequestHandler<RevokeModelCredentialCommand, Guid>
{
    private readonly IModelCredentialService _service;

    public RevokeModelCredentialCommandHandler(IModelCredentialService service) { _service = service; }

    public async Task<Guid> Handle(RevokeModelCredentialCommand request, CancellationToken cancellationToken) =>
        await _service.RevokeAsync(request.Id, cancellationToken).ConfigureAwait(false);
}

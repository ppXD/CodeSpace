using CodeSpace.Core.Services.Repositories;
using CodeSpace.Messages.Commands.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

public sealed class RelinkRepositoryCredentialCommandHandler : IRequestHandler<RelinkRepositoryCredentialCommand, Unit>
{
    private readonly IRepositoryService _service;

    public RelinkRepositoryCredentialCommandHandler(IRepositoryService service) { _service = service; }

    public async Task<Unit> Handle(RelinkRepositoryCredentialCommand request, CancellationToken cancellationToken)
    {
        await _service.RelinkCredentialAsync(request.RepositoryId, request.NewCredentialId, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

using CodeSpace.Core.Services.RepositoryBinding;
using CodeSpace.Messages.Commands.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

public sealed class UnbindRepositoryCommandHandler : IRequestHandler<UnbindRepositoryCommand, Unit>
{
    private readonly IRepositoryBindingService _service;

    public UnbindRepositoryCommandHandler(IRepositoryBindingService service) { _service = service; }

    public Task<Unit> Handle(UnbindRepositoryCommand request, CancellationToken cancellationToken) => _service.UnbindAsync(request.RepositoryId, cancellationToken);
}

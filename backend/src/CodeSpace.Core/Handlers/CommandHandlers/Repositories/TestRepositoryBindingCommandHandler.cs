using CodeSpace.Core.Services.RepositoryBinding;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Providers;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

public sealed class TestRepositoryBindingCommandHandler : IRequestHandler<TestRepositoryBindingCommand, CredentialProbeResult>
{
    private readonly IRepositoryBindingService _service;

    public TestRepositoryBindingCommandHandler(IRepositoryBindingService service) { _service = service; }

    public Task<CredentialProbeResult> Handle(TestRepositoryBindingCommand request, CancellationToken cancellationToken) => _service.TestAsync(request.RepositoryId, cancellationToken);
}

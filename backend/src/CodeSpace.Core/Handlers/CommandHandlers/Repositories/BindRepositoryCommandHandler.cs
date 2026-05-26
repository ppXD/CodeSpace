using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.RepositoryBinding;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

public sealed class BindRepositoryCommandHandler : IRequestHandler<BindRepositoryCommand, Guid>
{
    private readonly IRepositoryBindingService _service;
    private readonly ICurrentTeam _currentTeam;

    public BindRepositoryCommandHandler(IRepositoryBindingService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<Guid> Handle(BindRepositoryCommand request, CancellationToken cancellationToken)
    {
        // TeamMembershipAuthorizationBehavior runs before us — if it accepted the request,
        // _currentTeam.Id is guaranteed non-null.
        var bindRequest = new BindRepositoryRequest
        {
            TeamId = _currentTeam.Id!.Value,
            ProviderInstanceId = request.ProviderInstanceId,
            CredentialId = request.CredentialId,
            ProjectIdentifier = request.ProjectIdentifier
        };

        var repository = await _service.BindAsync(bindRequest, cancellationToken).ConfigureAwait(false);
        return repository.Id;
    }
}

using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.RepositoryBinding;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

/// <summary>
/// Thin Mediator → Service dispatcher (CLAUDE.md Rule 16). The all-or-nothing iteration and
/// per-item request expansion live on <see cref="IRepositoryBindingService.BindManyAsync"/>;
/// the handler only resolves <c>teamId</c> from <see cref="ICurrentTeam"/> and forwards.
/// </summary>
public sealed class BindRepositoriesBulkCommandHandler : IRequestHandler<BindRepositoriesBulkCommand, BulkBindResult>
{
    private readonly IRepositoryBindingService _service;
    private readonly ICurrentTeam _currentTeam;

    public BindRepositoriesBulkCommandHandler(IRepositoryBindingService service, ICurrentTeam currentTeam)
    {
        _service = service;
        _currentTeam = currentTeam;
    }

    public async Task<BulkBindResult> Handle(BindRepositoriesBulkCommand request, CancellationToken cancellationToken)
    {
        var bulk = new BindRepositoriesBulkRequest
        {
            TeamId = _currentTeam.Id!.Value,
            ProviderInstanceId = request.ProviderInstanceId,
            CredentialId = request.CredentialId,
            ProjectIdentifiers = request.ProjectIdentifiers,
            ProjectId = request.ProjectId,
        };

        return await _service.BindManyAsync(bulk, cancellationToken).ConfigureAwait(false);
    }
}

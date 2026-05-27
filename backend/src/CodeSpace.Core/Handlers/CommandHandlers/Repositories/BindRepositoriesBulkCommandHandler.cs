using CodeSpace.Core.Services.Identity;
using CodeSpace.Core.Services.RepositoryBinding;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

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
        var items = new List<BulkBindItemResult>();
        var teamId = _currentTeam.Id!.Value;

        foreach (var identifier in request.ProjectIdentifiers)
        {
            var bindRequest = new BindRepositoryRequest
            {
                TeamId = teamId,
                ProviderInstanceId = request.ProviderInstanceId,
                CredentialId = request.CredentialId,
                ProjectIdentifier = identifier,
                ProjectId = request.ProjectId,
            };

            var repo = await _service.BindAsync(bindRequest, cancellationToken).ConfigureAwait(false);
            items.Add(new BulkBindItemResult { ProjectIdentifier = identifier, RepositoryId = repo.Id });
        }

        return new BulkBindResult
        {
            Items = items,
            SuccessCount = items.Count,
            FailureCount = 0
        };
    }
}

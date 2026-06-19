using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Dtos.ModelCredentials;
using CodeSpace.Messages.Queries.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.ModelCredentials;

public sealed class ListCredentialedModelsQueryHandler : IRequestHandler<ListCredentialedModelsQuery, IReadOnlyList<CredentialedModelSummary>>
{
    private readonly IModelCredentialService _service;

    public ListCredentialedModelsQueryHandler(IModelCredentialService service) { _service = service; }

    public async Task<IReadOnlyList<CredentialedModelSummary>> Handle(ListCredentialedModelsQuery request, CancellationToken cancellationToken) =>
        await _service.ListModelsAsync(request.ModelCredentialId, cancellationToken).ConfigureAwait(false);
}

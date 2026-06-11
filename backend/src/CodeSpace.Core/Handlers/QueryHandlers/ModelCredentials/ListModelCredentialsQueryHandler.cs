using CodeSpace.Core.Services.Agents.ModelCredentials;
using CodeSpace.Messages.Dtos.ModelCredentials;
using CodeSpace.Messages.Queries.ModelCredentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.ModelCredentials;

public sealed class ListModelCredentialsQueryHandler : IRequestHandler<ListModelCredentialsQuery, IReadOnlyList<ModelCredentialSummary>>
{
    private readonly IModelCredentialService _service;

    public ListModelCredentialsQueryHandler(IModelCredentialService service) { _service = service; }

    public async Task<IReadOnlyList<ModelCredentialSummary>> Handle(ListModelCredentialsQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(request.Provider, cancellationToken).ConfigureAwait(false);
}

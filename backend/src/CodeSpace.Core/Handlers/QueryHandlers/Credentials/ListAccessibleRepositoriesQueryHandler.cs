using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Providers;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Credentials;

public sealed class ListAccessibleRepositoriesQueryHandler : IRequestHandler<ListAccessibleRepositoriesQuery, RemoteRepositoryPage>
{
    private readonly ICredentialService _service;

    public ListAccessibleRepositoriesQueryHandler(ICredentialService service) { _service = service; }

    public async Task<RemoteRepositoryPage> Handle(ListAccessibleRepositoriesQuery request, CancellationToken cancellationToken) =>
        await _service.ListAccessibleRepositoriesAsync(request.CredentialId, request.Search, request.Page, request.PerPage, cancellationToken).ConfigureAwait(false);
}

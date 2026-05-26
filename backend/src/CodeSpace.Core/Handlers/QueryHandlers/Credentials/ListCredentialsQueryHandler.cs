using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Credentials;

public sealed class ListCredentialsQueryHandler : IRequestHandler<ListCredentialsQuery, IReadOnlyList<CredentialSummary>>
{
    private readonly ICredentialService _service;

    public ListCredentialsQueryHandler(ICredentialService service) { _service = service; }

    public async Task<IReadOnlyList<CredentialSummary>> Handle(ListCredentialsQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(request.ProviderInstanceId, cancellationToken).ConfigureAwait(false);
}

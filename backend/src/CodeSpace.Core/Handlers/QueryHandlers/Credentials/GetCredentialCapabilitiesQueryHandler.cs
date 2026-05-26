using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Credentials;

public sealed class GetCredentialCapabilitiesQueryHandler : IRequestHandler<GetCredentialCapabilitiesQuery, CredentialCapabilitiesResponse>
{
    private readonly ICredentialService _service;

    public GetCredentialCapabilitiesQueryHandler(ICredentialService service) { _service = service; }

    public async Task<CredentialCapabilitiesResponse> Handle(GetCredentialCapabilitiesQuery request, CancellationToken cancellationToken) =>
        await _service.GetCapabilitiesAsync(request.CredentialId, cancellationToken).ConfigureAwait(false);
}

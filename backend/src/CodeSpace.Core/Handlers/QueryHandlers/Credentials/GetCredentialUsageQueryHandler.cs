using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Credentials;

public sealed class GetCredentialUsageQueryHandler : IRequestHandler<GetCredentialUsageQuery, CredentialUsage>
{
    private readonly ICredentialService _service;

    public GetCredentialUsageQueryHandler(ICredentialService service) { _service = service; }

    public async Task<CredentialUsage> Handle(GetCredentialUsageQuery request, CancellationToken cancellationToken) =>
        await _service.GetUsageAsync(request.CredentialId, cancellationToken).ConfigureAwait(false);
}

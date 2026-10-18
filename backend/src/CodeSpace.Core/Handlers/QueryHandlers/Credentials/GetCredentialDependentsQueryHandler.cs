using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Credentials;

public sealed class GetCredentialDependentsQueryHandler : IRequestHandler<GetCredentialDependentsQuery, CredentialDependents>
{
    private readonly ICredentialRelinkService _service;

    public GetCredentialDependentsQueryHandler(ICredentialRelinkService service) { _service = service; }

    public async Task<CredentialDependents> Handle(GetCredentialDependentsQuery request, CancellationToken cancellationToken) =>
        await _service.GetDependentsAsync(request.CredentialId, cancellationToken).ConfigureAwait(false);
}

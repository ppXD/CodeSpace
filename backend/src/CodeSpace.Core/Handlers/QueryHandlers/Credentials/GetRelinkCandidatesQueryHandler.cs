using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Credentials;

public sealed class GetRelinkCandidatesQueryHandler : IRequestHandler<GetRelinkCandidatesQuery, IReadOnlyList<RelinkSuggestion>>
{
    private readonly ICredentialRelinkService _service;

    public GetRelinkCandidatesQueryHandler(ICredentialRelinkService service) { _service = service; }

    public async Task<IReadOnlyList<RelinkSuggestion>> Handle(GetRelinkCandidatesQuery request, CancellationToken cancellationToken) =>
        await _service.GetCandidatesAsync(request.ProviderInstanceId, request.FromCredentialId, cancellationToken).ConfigureAwait(false);
}

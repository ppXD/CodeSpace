using CodeSpace.Core.Services.Credentials;
using CodeSpace.Messages.Dtos.Credentials;
using CodeSpace.Messages.Queries.Credentials;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Credentials;

public sealed class GetRelinkSuggestionsQueryHandler : IRequestHandler<GetRelinkSuggestionsQuery, IReadOnlyList<RelinkSuggestion>>
{
    private readonly ICredentialRelinkService _service;

    public GetRelinkSuggestionsQueryHandler(ICredentialRelinkService service) { _service = service; }

    public async Task<IReadOnlyList<RelinkSuggestion>> Handle(GetRelinkSuggestionsQuery request, CancellationToken cancellationToken) =>
        await _service.GetSuggestionsAsync(request.CredentialId, cancellationToken).ConfigureAwait(false);
}

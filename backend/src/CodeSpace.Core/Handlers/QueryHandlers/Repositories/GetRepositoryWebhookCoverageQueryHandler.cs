using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class GetRepositoryWebhookCoverageQueryHandler : IRequestHandler<GetRepositoryWebhookCoverageQuery, RepositoryWebhookCoverage>
{
    private readonly IConnectionWebhookCoverageReader _reader;

    public GetRepositoryWebhookCoverageQueryHandler(IConnectionWebhookCoverageReader reader) { _reader = reader; }

    public async Task<RepositoryWebhookCoverage> Handle(GetRepositoryWebhookCoverageQuery request, CancellationToken cancellationToken) =>
        await _reader.GetForRepositoryAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
}

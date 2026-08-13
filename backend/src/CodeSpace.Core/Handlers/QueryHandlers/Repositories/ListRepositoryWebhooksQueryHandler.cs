using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Dtos.Repositories;
using CodeSpace.Messages.Queries.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Repositories;

public sealed class ListRepositoryWebhooksQueryHandler : IRequestHandler<ListRepositoryWebhooksQuery, IReadOnlyList<RepositoryWebhookDetail>>
{
    private readonly IRepositoryWebhookService _service;

    public ListRepositoryWebhooksQueryHandler(IRepositoryWebhookService service) { _service = service; }

    public async Task<IReadOnlyList<RepositoryWebhookDetail>> Handle(ListRepositoryWebhooksQuery request, CancellationToken cancellationToken) =>
        await _service.ListAsync(request.RepositoryId, cancellationToken).ConfigureAwait(false);
}

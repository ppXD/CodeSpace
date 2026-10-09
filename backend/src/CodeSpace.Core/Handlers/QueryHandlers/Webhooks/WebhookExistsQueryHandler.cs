using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Queries.Webhooks;
using MediatR;

namespace CodeSpace.Core.Handlers.QueryHandlers.Webhooks;

public sealed class WebhookExistsQueryHandler : IRequestHandler<WebhookExistsQuery, bool>
{
    private readonly IWebhookLookup _lookup;

    public WebhookExistsQueryHandler(IWebhookLookup lookup) { _lookup = lookup; }

    public async Task<bool> Handle(WebhookExistsQuery request, CancellationToken cancellationToken) =>
        await _lookup.ExistsAsync(request.Scope, request.WebhookId, cancellationToken).ConfigureAwait(false);
}

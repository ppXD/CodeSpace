using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Commands.Webhooks;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Webhooks;

public sealed class ReceiveWebhookCommandHandler : IRequestHandler<ReceiveWebhookCommand, Unit>
{
    private readonly IWebhookIngestionService _ingestion;

    public ReceiveWebhookCommandHandler(IWebhookIngestionService ingestion) { _ingestion = ingestion; }

    public async Task<Unit> Handle(ReceiveWebhookCommand request, CancellationToken cancellationToken)
    {
        await _ingestion.IngestAsync(request.WebhookId, request.Body, request.Headers, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

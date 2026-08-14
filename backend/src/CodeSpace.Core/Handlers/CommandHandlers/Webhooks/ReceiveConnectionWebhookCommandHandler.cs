using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Commands.Webhooks;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Webhooks;

public sealed class ReceiveConnectionWebhookCommandHandler : IRequestHandler<ReceiveConnectionWebhookCommand, Unit>
{
    private readonly IConnectionWebhookIngestionService _ingestion;

    public ReceiveConnectionWebhookCommandHandler(IConnectionWebhookIngestionService ingestion) { _ingestion = ingestion; }

    public async Task<Unit> Handle(ReceiveConnectionWebhookCommand request, CancellationToken cancellationToken)
    {
        await _ingestion.IngestConnectionAsync(request.ConnectionWebhookId, request.Body, request.Headers, cancellationToken).ConfigureAwait(false);
        return Unit.Value;
    }
}

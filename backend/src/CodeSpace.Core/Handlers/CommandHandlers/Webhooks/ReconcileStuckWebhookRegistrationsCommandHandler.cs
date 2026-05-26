using CodeSpace.Core.Services.Webhooks.Registration;
using CodeSpace.Messages.Commands.Webhooks;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Webhooks;

/// <summary>
/// Rule 16 — thin handler. Delegates to <see cref="IStuckWebhookRegistrationReconcilerService"/>
/// and maps the service's domain summary onto the messages-layer response DTO.
/// </summary>
public sealed class ReconcileStuckWebhookRegistrationsCommandHandler : IRequestHandler<ReconcileStuckWebhookRegistrationsCommand, ReconcileStuckWebhookRegistrationsResponse>
{
    private readonly IStuckWebhookRegistrationReconcilerService _service;

    public ReconcileStuckWebhookRegistrationsCommandHandler(IStuckWebhookRegistrationReconcilerService service) { _service = service; }

    public async Task<ReconcileStuckWebhookRegistrationsResponse> Handle(ReconcileStuckWebhookRegistrationsCommand request, CancellationToken cancellationToken)
    {
        var summary = await _service.ReconcileAsync(cancellationToken).ConfigureAwait(false);
        return new ReconcileStuckWebhookRegistrationsResponse
        {
            RedispatchedFromPending = summary.RedispatchedFromPending,
            RevertedFromEnqueued = summary.RevertedFromEnqueued,
            RevertedFromRegistering = summary.RevertedFromRegistering,
            RevivedFromFailed = summary.RevivedFromFailed,
        };
    }
}

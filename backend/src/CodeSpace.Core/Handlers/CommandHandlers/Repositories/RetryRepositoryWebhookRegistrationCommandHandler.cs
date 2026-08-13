using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

public sealed class RetryRepositoryWebhookRegistrationCommandHandler : IRequestHandler<RetryRepositoryWebhookRegistrationCommand, RepositoryWebhookDetail>
{
    private readonly IRepositoryWebhookService _service;

    public RetryRepositoryWebhookRegistrationCommandHandler(IRepositoryWebhookService service) { _service = service; }

    public async Task<RepositoryWebhookDetail> Handle(RetryRepositoryWebhookRegistrationCommand request, CancellationToken cancellationToken) =>
        await _service.RetryRegistrationAsync(request.RepositoryId, request.WebhookId, cancellationToken).ConfigureAwait(false);
}

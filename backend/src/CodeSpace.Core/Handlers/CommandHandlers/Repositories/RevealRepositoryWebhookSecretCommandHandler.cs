using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Commands.Repositories;
using CodeSpace.Messages.Dtos.Repositories;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Repositories;

public sealed class RevealRepositoryWebhookSecretCommandHandler : IRequestHandler<RevealRepositoryWebhookSecretCommand, RepositoryWebhookSecret>
{
    private readonly IRepositoryWebhookService _service;

    public RevealRepositoryWebhookSecretCommandHandler(IRepositoryWebhookService service) { _service = service; }

    public async Task<RepositoryWebhookSecret> Handle(RevealRepositoryWebhookSecretCommand request, CancellationToken cancellationToken) =>
        await _service.RevealSecretAsync(request.RepositoryId, request.WebhookId, cancellationToken).ConfigureAwait(false);
}

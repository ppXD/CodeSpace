using CodeSpace.Core.Services.Webhooks;
using CodeSpace.Messages.Commands.Webhooks;
using MediatR;

namespace CodeSpace.Core.Handlers.CommandHandlers.Webhooks;

/// <summary>Rule 16 — thin handler. The expiry predicate and the batched delete live in <see cref="IWebhookClaimStore"/>.</summary>
public sealed class PurgeExpiredWebhookClaimsCommandHandler : IRequestHandler<PurgeExpiredWebhookClaimsCommand, PurgeExpiredWebhookClaimsResponse>
{
    private readonly IWebhookClaimStore _claims;

    public PurgeExpiredWebhookClaimsCommandHandler(IWebhookClaimStore claims) { _claims = claims; }

    public async Task<PurgeExpiredWebhookClaimsResponse> Handle(PurgeExpiredWebhookClaimsCommand request, CancellationToken cancellationToken)
    {
        var deleted = await _claims.PurgeExpiredAsync(cancellationToken).ConfigureAwait(false);

        return new PurgeExpiredWebhookClaimsResponse { Deleted = deleted };
    }
}

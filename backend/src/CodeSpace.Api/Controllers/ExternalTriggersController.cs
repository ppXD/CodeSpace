using CodeSpace.Api.Extensions;
using CodeSpace.Api.Http;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using static CodeSpace.Api.Controllers.WebhooksController;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Anonymous ingress: the per-node key is the only credential, not a user or team session. Slugs are enumerable;
/// every unavailable endpoint therefore has the same 404, revealing none of its deletion or activation state.
/// Accepted calls remain 2xx even when admission cannot stage a run, so an unfixable refusal does not invite retries.
/// Payload rejection still answers 400, with the ingress's errors rather than a second validation contract here.
/// </summary>
[ApiController]
[Route("api/ext")]
[AllowAnonymous]
[EnableRateLimiting(WebhookIngressRateLimitExtension.PolicyName)]
public class ExternalTriggersController : ControllerBase
{
    private readonly IMediator _mediator;

    public ExternalTriggersController(IMediator mediator) { _mediator = mediator; }

    [HttpPost("{slug}/run")]
    [RequestSizeLimit(MaxDeliveryBodyBytes)]
    public async Task<IActionResult> Run([FromRoute] string slug, [FromQuery] string? key, CancellationToken cancellationToken)
    {
        var body = await Request.ReadBoundedBodyAsync(MaxDeliveryBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body == null) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        var authorization = Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
        var apiKey = Request.Headers["X-Api-Key"].ToString();
        var presentedKey = !string.IsNullOrEmpty(bearer) ? bearer : !string.IsNullOrEmpty(apiKey) ? apiKey : string.IsNullOrEmpty(key) ? null : key;

        var idempotencyHeader = Request.Headers["Idempotency-Key"].ToString();
        var idempotencyKey = string.IsNullOrWhiteSpace(idempotencyHeader) ? null : idempotencyHeader;
        var idempotencyRejected = idempotencyKey != null && !ExternalTriggerIdempotency.IsWellFormed(idempotencyKey);
        var outcome = await _mediator.Send(new ReceiveExternalTriggerCommand
        {
            Slug = slug,
            PresentedKey = presentedKey,
            IdempotencyKey = idempotencyKey,
            IdempotencyRejected = idempotencyRejected,
            RawBody = body,
        }, cancellationToken).ConfigureAwait(false);

        return outcome.Kind switch
        {
            ExternalTriggerDeliveryKind.NotFound => NotFound(),
            ExternalTriggerDeliveryKind.Unauthorized => Unauthorized(),
            ExternalTriggerDeliveryKind.InvalidPayload => BadRequest(new { errors = outcome.Errors }),
            ExternalTriggerDeliveryKind.Deduplicated => Ok(new { runId = outcome.RunId, deduplicated = true }),
            ExternalTriggerDeliveryKind.Accepted => StatusCode(StatusCodes.Status202Accepted, new { runId = outcome.RunId == Guid.Empty ? (Guid?)null : outcome.RunId, deduplicated = false }),
            _ => throw new InvalidOperationException($"Unknown external trigger outcome: {outcome.Kind}"),
        };
    }
}

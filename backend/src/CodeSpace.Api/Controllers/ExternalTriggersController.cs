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
/// Capacity admission answers 429 with Retry-After; deterministic authority or completion-policy refusals answer 403 or 422.
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

    [HttpPost("{slug:length(1,64)}/run")]
    [RequestSizeLimit(MaxDeliveryBodyBytes)]
    public async Task<IActionResult> Run([FromRoute] string slug, CancellationToken cancellationToken)
    {
        var authorization = Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
        var apiKey = Request.Headers["X-Api-Key"].ToString();
        var presentedKey = !string.IsNullOrEmpty(bearer) ? bearer : !string.IsNullOrEmpty(apiKey) ? apiKey : null;

        var verification = await _mediator.Send(new VerifyExternalTriggerCommand { Slug = slug, PresentedKey = presentedKey }, cancellationToken).ConfigureAwait(false);
        if (verification.Kind is ExternalTriggerDeliveryKind.NotFound or ExternalTriggerDeliveryKind.Unauthorized)
            return verification.Kind == ExternalTriggerDeliveryKind.NotFound ? NotFound() : Unauthorized();

        var body = await Request.ReadBoundedBodyAsync(MaxDeliveryBodyBytes, cancellationToken).ConfigureAwait(false);
        if (body == null) return StatusCode(StatusCodes.Status413PayloadTooLarge);

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
            ExternalTriggerDeliveryKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { errors = outcome.Errors }),
            ExternalTriggerDeliveryKind.Unprocessable => StatusCode(StatusCodes.Status422UnprocessableEntity, new { errors = outcome.Errors }),
            ExternalTriggerDeliveryKind.InvalidPayload => BadRequest(new { errors = outcome.Errors }),
            ExternalTriggerDeliveryKind.RateLimited => RateLimited(),
            ExternalTriggerDeliveryKind.Deduplicated => Ok(new { runId = outcome.RunId, deduplicated = true }),
            ExternalTriggerDeliveryKind.Accepted => StatusCode(StatusCodes.Status202Accepted, new { runId = outcome.RunId == Guid.Empty ? (Guid?)null : outcome.RunId, deduplicated = false }),
            _ => throw new InvalidOperationException($"Unknown external trigger outcome: {outcome.Kind}"),
        };
    }

    private IActionResult RateLimited()
    {
        Response.Headers.RetryAfter = ((int)ExternalTriggerRunAdmission.Window.TotalSeconds).ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new { retryAfterSeconds = (int)ExternalTriggerRunAdmission.Window.TotalSeconds });
    }
}

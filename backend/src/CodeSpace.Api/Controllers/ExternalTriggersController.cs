using CodeSpace.Api.Extensions;
using CodeSpace.Api.Http;
using CodeSpace.Core.Services.Workflows.ExternalTriggers;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using CodeSpace.Messages.Dtos.Workflows;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using CodeSpace.Messages.Failures;
using System.Text.Json;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Anonymous ingress authenticates before reading the bounded body or opening the receive transaction.
/// </summary>
[ApiController]
[Route("api/ext")]
[AllowAnonymous]
[EnableRateLimiting(WebhookIngressRateLimitExtension.PolicyName)]
public class ExternalTriggersController : ControllerBase
{
    private readonly IMediator _mediator;

    public ExternalTriggersController(IMediator mediator) { _mediator = mediator; }

    [HttpPost("{triggerId:guid}/run")]
    [RequestSizeLimit(BoundedJsonBodyReader.MaxBytes + 1)]
    public async Task<IActionResult> Run([FromRoute] Guid triggerId, CancellationToken cancellationToken)
    {
        var authorization = Request.Headers.Authorization.ToString();
        var bearer = authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? authorization[7..].Trim() : null;
        var apiKey = Request.Headers["X-Api-Key"].ToString();
        var presentedKey = !string.IsNullOrEmpty(bearer) ? bearer : !string.IsNullOrEmpty(apiKey) ? apiKey : null;

        var verification = await _mediator.Send(new VerifyExternalTriggerQuery { TriggerId = triggerId, PresentedKey = presentedKey }, cancellationToken).ConfigureAwait(false);
        if (verification.Kind is ExternalTriggerDeliveryKind.NotFound or ExternalTriggerDeliveryKind.Unauthorized)
            return Unauthorized(new { code = FailureCodes.Unauthorized, message = "Invalid external trigger credentials." });

        JsonDocument document;
        try { document = await BoundedJsonBodyReader.ReadAsync(Request, cancellationToken).ConfigureAwait(false); }
        catch (BoundedJsonBodyReader.BodyTooLargeException)
        { return StatusCode(StatusCodes.Status413PayloadTooLarge, new { code = FailureCodes.ExternalTriggerBodyTooLarge, message = "Request body exceeds 1048576 bytes." }); }
        catch (JsonException)
        { return BadRequest(new { code = FailureCodes.InvalidRequest, message = "Invalid external trigger input.", errors = new[] { "$: body must be a JSON object" } }); }
        using var body = document;

        var idempotencyHeader = Request.Headers["Idempotency-Key"].ToString();
        var idempotencyKey = string.IsNullOrWhiteSpace(idempotencyHeader) ? null : idempotencyHeader;
        var idempotencyRejected = idempotencyKey != null && !ExternalTriggerIdempotency.IsWellFormed(idempotencyKey);
        var outcome = await _mediator.Send(new ReceiveExternalTriggerCommand
        {
            TriggerId = triggerId,
            PresentedKey = presentedKey,
            IdempotencyKey = idempotencyKey,
            IdempotencyRejected = idempotencyRejected,
            Body = body.RootElement,
        }, cancellationToken).ConfigureAwait(false);

        return outcome.Kind switch
        {
            ExternalTriggerDeliveryKind.NotFound => NotFound(new { code = FailureCodes.NotFound, message = "External trigger is unavailable." }),
            ExternalTriggerDeliveryKind.Unauthorized => Unauthorized(new { code = FailureCodes.Unauthorized, message = "Invalid external trigger credentials." }),
            ExternalTriggerDeliveryKind.Forbidden => StatusCode(StatusCodes.Status403Forbidden, new { code = FailureCodes.Forbidden, message = "External trigger execution is forbidden." }),
            ExternalTriggerDeliveryKind.Unprocessable => StatusCode(StatusCodes.Status422UnprocessableEntity, new { code = outcome.Code ?? FailureCodes.ExternalTriggerCompletionRefused,
                message = outcome.Code == FailureCodes.ExternalTriggerIdempotencyConflict ? "Idempotency key was already used with different content." : "External trigger execution was refused." }),
            ExternalTriggerDeliveryKind.InvalidPayload => BadRequest(new { code = FailureCodes.InvalidRequest, message = "Invalid external trigger input.", errors = outcome.Errors }),
            ExternalTriggerDeliveryKind.RateLimited => RateLimited(),
            ExternalTriggerDeliveryKind.Deduplicated => Ok(new { runId = outcome.RunId, deduplicated = true }),
            ExternalTriggerDeliveryKind.Accepted => StatusCode(StatusCodes.Status202Accepted, new { runId = outcome.RunId, deduplicated = false }),
            _ => throw new InvalidOperationException($"Unknown external trigger outcome: {outcome.Kind}"),
        };
    }

    private IActionResult RateLimited()
    {
        Response.Headers.RetryAfter = ((int)ExternalTriggerRunAdmission.Window.TotalSeconds).ToString();
        return StatusCode(StatusCodes.Status429TooManyRequests, new { code = FailureCodes.RateLimited, message = "External trigger run capacity is exhausted." });
    }
}

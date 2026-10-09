using CodeSpace.Api.Extensions;
using CodeSpace.Api.Http;
using CodeSpace.Core.Services.Webhooks.Exceptions;
using CodeSpace.Messages.Commands.Webhooks;
using CodeSpace.Messages.Enums;
using CodeSpace.Messages.Queries.Webhooks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Provider deliveries. Anonymous — the per-hook signature is the proof — so everything here is bounded before it is
/// authenticated: the rate limiter answers a flood before the action runs, an id naming no hook is answered before the
/// body is read, and a body is read only up to <see cref="MaxDeliveryBodyBytes"/>.
/// </summary>
[ApiController]
[Route("api/webhooks")]
[AllowAnonymous]
[EnableRateLimiting(WebhookIngressRateLimitExtension.PolicyName)]
public class WebhooksController : ControllerBase
{
    /// <summary>GitHub caps a webhook payload at 25 MB and drops a larger event rather than send it; GitLab truncates its lists well below that. Nothing a provider sends is bigger.</summary>
    public const long MaxDeliveryBodyBytes = 25L * 1024 * 1024;

    private readonly IMediator _mediator;

    public WebhooksController(IMediator mediator) { _mediator = mediator; }

    [HttpPost("{webhookId:guid}")]
    [RequestSizeLimit(MaxDeliveryBodyBytes)]
    public async Task<IActionResult> Receive(Guid webhookId, CancellationToken cancellationToken)
    {
        if (!await HookExistsAsync(ProviderWebhookScope.Repository, webhookId, cancellationToken).ConfigureAwait(false)) return NotFound();

        var delivery = await ReadDeliveryAsync(cancellationToken).ConfigureAwait(false);

        if (delivery == null) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        return await DispatchAsync(new ReceiveWebhookCommand
        {
            WebhookId = webhookId,
            Body = delivery.Value.Body,
            Headers = delivery.Value.Headers
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A group / organization delivery. Its own literal segment BEFORE the <c>{webhookId:guid}</c>
    /// route above, which is what keeps the two apart: the id here names the hook and not the
    /// repository, and a delivery that resolved to the repository-scoped route would be looked up in
    /// the wrong table and answered 404.
    /// </summary>
    [HttpPost("connection/{connectionWebhookId:guid}")]
    [RequestSizeLimit(MaxDeliveryBodyBytes)]
    public async Task<IActionResult> ReceiveConnection(Guid connectionWebhookId, CancellationToken cancellationToken)
    {
        if (!await HookExistsAsync(ProviderWebhookScope.Connection, connectionWebhookId, cancellationToken).ConfigureAwait(false)) return NotFound();

        var delivery = await ReadDeliveryAsync(cancellationToken).ConfigureAwait(false);

        if (delivery == null) return StatusCode(StatusCodes.Status413PayloadTooLarge);

        return await DispatchAsync(new ReceiveConnectionWebhookCommand
        {
            ConnectionWebhookId = connectionWebhookId,
            Body = delivery.Value.Body,
            Headers = delivery.Value.Headers
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> HookExistsAsync(ProviderWebhookScope scope, Guid webhookId, CancellationToken cancellationToken) =>
        await _mediator.Send(new WebhookExistsQuery { WebhookId = webhookId, Scope = scope }, cancellationToken).ConfigureAwait(false);

    /// <summary>Null when the body is larger than <see cref="MaxDeliveryBodyBytes"/>.</summary>
    private async Task<(string Body, Dictionary<string, string> Headers)?> ReadDeliveryAsync(CancellationToken cancellationToken)
    {
        var body = await Request.ReadBoundedBodyAsync(MaxDeliveryBodyBytes, cancellationToken).ConfigureAwait(false);

        if (body == null) return null;

        return (body, Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()));
    }

    /// <summary>
    /// The one status mapping, shared by both routes so a group delivery and a project delivery
    /// cannot answer differently for the same failure. 200 for anything the service handled — a
    /// provider reads 5xx as "retry" and GitLab disables a hook that keeps failing, which would take
    /// out every repository the hook covers. 400 for a signed delivery missing the id its provider
    /// always sends — malformed for that provider, and never something the provider itself sends.
    /// </summary>
    private async Task<IActionResult> DispatchAsync(IRequest<Unit> command, CancellationToken cancellationToken)
    {
        try
        {
            await _mediator.Send(command, cancellationToken).ConfigureAwait(false);

            return Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return Unauthorized();
        }
        catch (WebhookDeliveryUnidentifiedException)
        {
            return BadRequest();
        }
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }
}

using CodeSpace.Messages.Commands.Webhooks;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

[ApiController]
[Route("api/webhooks")]
[AllowAnonymous]
public class WebhooksController : ControllerBase
{
    private readonly IMediator _mediator;

    public WebhooksController(IMediator mediator) { _mediator = mediator; }

    [HttpPost("{webhookId:guid}")]
    public async Task<IActionResult> Receive(Guid webhookId, CancellationToken cancellationToken)
    {
        var delivery = await ReadDeliveryAsync(cancellationToken).ConfigureAwait(false);

        return await DispatchAsync(new ReceiveWebhookCommand
        {
            WebhookId = webhookId,
            Body = delivery.Body,
            Headers = delivery.Headers
        }, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// A group / organization delivery. Its own literal segment BEFORE the <c>{webhookId:guid}</c>
    /// route above, which is what keeps the two apart: the id here names the hook and not the
    /// repository, and a delivery that resolved to the repository-scoped route would be looked up in
    /// the wrong table and answered 404.
    /// </summary>
    [HttpPost("connection/{connectionWebhookId:guid}")]
    public async Task<IActionResult> ReceiveConnection(Guid connectionWebhookId, CancellationToken cancellationToken)
    {
        var delivery = await ReadDeliveryAsync(cancellationToken).ConfigureAwait(false);

        return await DispatchAsync(new ReceiveConnectionWebhookCommand
        {
            ConnectionWebhookId = connectionWebhookId,
            Body = delivery.Body,
            Headers = delivery.Headers
        }, cancellationToken).ConfigureAwait(false);
    }

    private async Task<(string Body, Dictionary<string, string> Headers)> ReadDeliveryAsync(CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);

        return (body, Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString()));
    }

    /// <summary>
    /// The one status mapping, shared by both routes so a group delivery and a project delivery
    /// cannot answer differently for the same failure. 200 for anything the service handled — a
    /// provider reads 5xx as "retry" and GitLab disables a hook that keeps failing, which would take
    /// out every repository the hook covers.
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
        catch (InvalidOperationException)
        {
            return NotFound();
        }
    }
}

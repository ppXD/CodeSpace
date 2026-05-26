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
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var headers = Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString());

        try
        {
            await _mediator.Send(new ReceiveWebhookCommand
            {
                WebhookId = webhookId,
                Body = body,
                Headers = headers
            }, cancellationToken).ConfigureAwait(false);

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

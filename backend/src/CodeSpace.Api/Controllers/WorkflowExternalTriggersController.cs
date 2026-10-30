using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

[ApiController]
[Route("api/workflows/external-triggers")]
public sealed class WorkflowExternalTriggersController(IMediator mediator) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListWorkflowExternalTriggersQuery query, CancellationToken cancellationToken)
    {
        var result = await mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("{triggerId:guid}/key")]
    public async Task<IActionResult> Reveal([FromRoute] Guid triggerId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var key = await mediator.Send(new RevealExternalTriggerKeyCommand { TriggerId = triggerId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { key });
    }

    [HttpPost("{triggerId:guid}/rotate-key")]
    public async Task<IActionResult> Rotate([FromRoute] Guid triggerId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "private, no-store";
        var key = await mediator.Send(new RotateExternalTriggerKeyCommand { TriggerId = triggerId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { key });
    }

}

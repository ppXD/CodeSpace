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

    [HttpGet("{slug}/key")]
    public async Task<IActionResult> Reveal([FromRoute] string slug, CancellationToken cancellationToken)
    {
        var key = await mediator.Send(new RevealExternalTriggerKeyCommand { Slug = slug }, cancellationToken).ConfigureAwait(false);
        return Ok(new { key });
    }

    [HttpPost("{slug}/rotate-key")]
    public async Task<IActionResult> Rotate([FromRoute] string slug, CancellationToken cancellationToken)
    {
        var key = await mediator.Send(new RotateExternalTriggerKeyCommand { Slug = slug }, cancellationToken).ConfigureAwait(false);
        return Ok(new { key });
    }

    [HttpPost("{slug}/rename")]
    public async Task<IActionResult> Rename([FromRoute] string slug, [FromBody] RenameExternalTriggerCommand command, CancellationToken cancellationToken)
    {
        await mediator.Send(command with { Slug = slug }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

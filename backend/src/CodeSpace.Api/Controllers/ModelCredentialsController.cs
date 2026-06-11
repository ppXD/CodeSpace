using CodeSpace.Messages.Commands.ModelCredentials;
using CodeSpace.Messages.Queries.ModelCredentials;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

[ApiController]
[Route("api/model-credentials")]
public class ModelCredentialsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ModelCredentialsController(IMediator mediator) { _mediator = mediator; }

    // All endpoints require X-Team-Id header (enforced by pipeline behaviors). The secret is never returned.

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListModelCredentialsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Add([FromBody] AddModelCredentialCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(new { id });
    }

    [HttpPut("{credentialId:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid credentialId, [FromBody] UpdateModelCredentialCommand command, CancellationToken cancellationToken)
    {
        // Route id is authoritative; merge into the command so the body can't target a different credential.
        var id = await _mediator.Send(command with { Id = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { id });
    }

    [HttpPost("{credentialId:guid}/revoke")]
    public async Task<IActionResult> Revoke([FromRoute] Guid credentialId, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(new RevokeModelCredentialCommand { Id = credentialId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { id });
    }
}

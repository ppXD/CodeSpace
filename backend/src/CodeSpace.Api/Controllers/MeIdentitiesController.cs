using CodeSpace.Messages.Commands.Identity;
using CodeSpace.Messages.Queries.Identity;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// The caller's OWN provider identities (Model B) — link via PAT / list / unlink. Team scope comes
/// from <c>X-Team-Id</c> (the provider instance is team-scoped) and the MediatR pipeline vets
/// membership before the handler runs. Commands / queries bind directly per Rule 17. OAuth-based
/// linking is added under this same route in a later PR.
/// </summary>
[ApiController]
[Route("api/me/identities")]
public class MeIdentitiesController : ControllerBase
{
    private readonly IMediator _mediator;

    public MeIdentitiesController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListMyProviderIdentitiesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPost("pat")]
    public async Task<IActionResult> LinkByPat([FromBody] LinkProviderIdentityByPatCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpDelete("{identityId:guid}")]
    public async Task<IActionResult> Unlink([FromRoute] Guid identityId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new UnlinkProviderIdentityCommand { IdentityId = identityId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

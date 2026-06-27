using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the team-scoped Library/store of imported packs (the agent + skill source libraries).
/// Team scope comes from <c>X-Team-Id</c>; the MediatR pipeline vets membership before the handler runs.
/// </summary>
[ApiController]
[Route("api/packs")]
public class PacksController : ControllerBase
{
    private readonly IMediator _mediator;

    public PacksController(IMediator mediator) { _mediator = mediator; }

    /// <summary>The team's imported packs (the store's source rail) with freshness + artifact counts.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListPacksQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>One pack with its agents + skills (the store detail pane).</summary>
    [HttpGet("{packId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid packId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetPackQuery { PackId = packId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }
}

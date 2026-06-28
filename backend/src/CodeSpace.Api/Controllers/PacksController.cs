using CodeSpace.Messages.Commands.Agents;
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

    /// <summary>One server-side page of a pack's store artifacts of a single kind (the paginated detail tab + the pickers), optionally name/handle-filtered. <c>GET /api/packs/{id}/artifacts?kind=Agent&amp;search=&amp;page=0&amp;pageSize=20</c>.</summary>
    [HttpGet("{packId:guid}/artifacts")]
    public async Task<IActionResult> ListArtifacts([FromRoute] Guid packId, [FromQuery] ListPackArtifactsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { PackId = packId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Re-pull a pack from its saved source — refresh its already-imported artifacts and return what changed (up-to-date / updated) plus the discovered-but-not-imported artifacts to add.</summary>
    [HttpPost("{packId:guid}/sync")]
    public async Task<IActionResult> Sync([FromRoute] Guid packId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new SyncPackCommand { PackId = packId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}

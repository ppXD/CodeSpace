using CodeSpace.Messages.Queries.Artifacts;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Read surface for content-addressed artifacts (D2). <c>GET /api/artifacts/{artifactId}</c> returns the raw
/// bytes (e.g. a large unified diff offloaded from an agent run's <c>AgentRunResult.PatchArtifactId</c>). Team
/// scope is resolved from <c>ICurrentTeam</c> in the handler (<see cref="GetArtifactQuery"/> carries
/// <c>IRequireTeamMembership</c>), never the route — a foreign / absent id 404-conflates (no existence leak).
/// </summary>
[ApiController]
[Route("api/artifacts")]
public class ArtifactsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ArtifactsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet("{artifactId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid artifactId, CancellationToken cancellationToken)
    {
        var artifact = await _mediator.Send(new GetArtifactQuery { ArtifactId = artifactId }, cancellationToken).ConfigureAwait(false);

        return artifact == null ? NotFound() : File(artifact.Bytes, artifact.ContentType);
    }
}

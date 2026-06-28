using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the team-scoped skill library. Read-only for now — the editor's skill-binding picker reads
/// it. Team scope comes from <c>X-Team-Id</c>; the MediatR pipeline vets membership before the handler runs.
/// </summary>
[ApiController]
[Route("api/skills")]
public class SkillsController : ControllerBase
{
    private readonly IMediator _mediator;

    public SkillsController(IMediator mediator) { _mediator = mediator; }

    /// <summary>The team's active skills (Level-1 summaries) — the editor's skill-binding picker.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListSkillsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}

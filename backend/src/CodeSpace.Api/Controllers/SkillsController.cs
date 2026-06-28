using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the team-scoped skill library — list (the editor's skill-binding picker), the detail read
/// (the Library detail modal), and soft-delete. Team scope comes from <c>X-Team-Id</c>; the MediatR pipeline
/// vets membership before the handler runs.
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

    /// <summary>One skill with its SKILL.md body (the Library detail modal). 404 when not in the caller's team.</summary>
    [HttpGet("{skillDefinitionId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid skillDefinitionId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetSkillQuery { SkillDefinitionId = skillDefinitionId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>Instantiate a new working (bindable) skill by copying a Library store snapshot — how binding a Library skill works. Returns the new skill id.</summary>
    [HttpPost("from-store")]
    public async Task<IActionResult> InstantiateFromStore([FromBody] InstantiateSkillFromStoreCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { skillDefinitionId = id }, new { id });
    }

    /// <summary>Author a new skill directly INTO the Library (a store entry under the team's Custom pack). Returns the new id.</summary>
    [HttpPost("library")]
    public async Task<IActionResult> AuthorIntoLibrary([FromBody] AuthorStoreSkillCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { skillDefinitionId = id }, new { id });
    }

    /// <summary>Soft-delete a skill.</summary>
    [HttpDelete("{skillDefinitionId:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid skillDefinitionId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteSkillCommand { SkillDefinitionId = skillDefinitionId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

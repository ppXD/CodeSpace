using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Queries.Variables;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for project-scoped variables. Mirror of <see cref="TeamVariablesController"/>
/// and <see cref="WorkflowVariablesController"/>. Project is identified by route id; team by
/// <c>X-Team-Id</c> header.
/// </summary>
[ApiController]
[Route("api/projects/{projectId:guid}/variables")]
public class ProjectVariablesController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProjectVariablesController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List([FromRoute] Guid projectId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListProjectVariablesQuery { ProjectId = projectId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPut("{name}")]
    public async Task<IActionResult> Set([FromRoute] Guid projectId, [FromRoute] string name, [FromBody] SetProjectVariableCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { ProjectId = projectId, Name = name }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete([FromRoute] Guid projectId, [FromRoute] string name, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteProjectVariableCommand { ProjectId = projectId, Name = name }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

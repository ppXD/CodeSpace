using CodeSpace.Messages.Commands.Projects;
using CodeSpace.Messages.Queries.Projects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for Projects (variable namespaces). Team comes from <c>X-Team-Id</c>.
/// </summary>
[ApiController]
[Route("api/projects")]
public class ProjectsController : ControllerBase
{
    private readonly IMediator _mediator;

    public ProjectsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListProjectsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("{projectId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid projectId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetProjectQuery { ProjectId = projectId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProjectCommand command, CancellationToken cancellationToken)
    {
        var projectId = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(new { projectId });
    }

    [HttpPut("{projectId:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid projectId, [FromBody] UpdateProjectCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { ProjectId = projectId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("{projectId:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid projectId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteProjectCommand { ProjectId = projectId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

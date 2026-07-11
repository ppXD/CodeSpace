using CodeSpace.Messages.Commands.Projects;
using CodeSpace.Messages.Queries.Projects;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for team-scoped projects. List / get / create / update / delete.
/// Team scope comes from <c>X-Team-Id</c>; the MediatR pipeline behaviour vets membership
/// before the handler runs.
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

    /// <summary>
    /// Read a single project by a URL reference — either its GUID (legacy link) or its
    /// team-unique slug (canonical clean URL, e.g. <c>/api/projects/squid</c>). The response
    /// carries the canonical <c>Slug</c> so the router can redirect a legacy-GUID URL to the
    /// slug URL. Mutations (PUT/DELETE) stay GUID-keyed — the caller already holds the id.
    /// </summary>
    [HttpGet("{idOrSlug}")]
    public async Task<IActionResult> Get([FromRoute] string idOrSlug, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetProjectByRefQuery { IdOrSlug = idOrSlug }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateProjectCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { idOrSlug = id.ToString() }, new { id });
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

    /// <summary>
    /// Move an existing repository INTO this project. Drives the project-detail
    /// Repositories tab's row-hover "Move to another project" action; the body
    /// carries the repository id, the route carries the target project id.
    /// </summary>
    [HttpPost("{projectId:guid}/repositories/{repositoryId:guid}/move-here")]
    public async Task<IActionResult> MoveRepositoryHere([FromRoute] Guid projectId, [FromRoute] Guid repositoryId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new MoveRepositoryToProjectCommand
        {
            RepositoryId = repositoryId,
            TargetProjectId = projectId,
        }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

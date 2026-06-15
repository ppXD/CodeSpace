using CodeSpace.Messages.Commands.Tasks;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the GENERIC L1 task launcher (Rule 18.2 — a task-first surface that composes WITH the
/// workflows engine, not a part of it). <c>POST /api/tasks</c> binds the <see cref="LaunchTaskCommand"/> directly
/// from the body (Rule 17), dispatches via the mediator, and returns the launch result (run id + route +
/// projection). <c>IRequireTeamMembership</c> on the command drives team scope; the team + actor are resolved from
/// <c>ICurrentTeam</c> / <c>ICurrentUser</c> in the handler, never this body.
/// </summary>
[ApiController]
[Route("api/tasks")]
public class TasksController : ControllerBase
{
    private readonly IMediator _mediator;

    public TasksController(IMediator mediator) { _mediator = mediator; }

    [HttpPost]
    public async Task<IActionResult> Launch([FromBody] LaunchTaskCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}

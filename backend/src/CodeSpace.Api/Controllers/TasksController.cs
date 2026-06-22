using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Queries.Tasks;
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

    /// <summary>
    /// The background-tasks UI phase tree for one task run — the merged, Order-sorted phases the projector derives
    /// from the run's durable substrate (structural nodes + supervisor decisions). A harmless READ: team-scoped via
    /// <c>ICurrentTeam</c> (the X-Team-Id header, never the route), and a foreign / absent run is 404-conflated (the
    /// projector returns null → NotFound — existence is never leaked). No flag.
    /// </summary>
    [HttpGet("{runId:guid}/phases")]
    public async Task<IActionResult> GetPhases([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetTaskRunPhasesQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);

        return result == null ? NotFound() : Ok(result);
    }
}

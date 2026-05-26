using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Queries.Variables;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for team-scoped variables. Covers both plain types and secrets behind
/// one endpoint family. Team scope comes from <c>X-Team-Id</c>.
/// </summary>
[ApiController]
[Route("api/team-variables")]
public class TeamVariablesController : ControllerBase
{
    private readonly IMediator _mediator;

    public TeamVariablesController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListTeamVariablesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPut("{name}")]
    public async Task<IActionResult> Set([FromRoute] string name, [FromBody] SetTeamVariableCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { Name = name }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete([FromRoute] string name, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteTeamVariableCommand { Name = name }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

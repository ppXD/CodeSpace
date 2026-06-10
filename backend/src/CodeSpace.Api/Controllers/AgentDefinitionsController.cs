using CodeSpace.Messages.Commands.Agents;
using CodeSpace.Messages.Queries.Agents;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the team-scoped Agents library (Agent personas). List / get / create / update /
/// delete. Team scope comes from <c>X-Team-Id</c>; the MediatR pipeline behaviour vets membership
/// before the handler runs. Records bind directly (Rule 17) — route ids merge in via <c>with</c>.
/// </summary>
[ApiController]
[Route("api/agent-definitions")]
public class AgentDefinitionsController : ControllerBase
{
    private readonly IMediator _mediator;

    public AgentDefinitionsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListAgentDefinitionsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("{agentDefinitionId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetAgentDefinitionQuery { AgentDefinitionId = agentDefinitionId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateAgentDefinitionCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return CreatedAtAction(nameof(Get), new { agentDefinitionId = id }, new { id });
    }

    [HttpPut("{agentDefinitionId:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid agentDefinitionId, [FromBody] UpdateAgentDefinitionCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { AgentDefinitionId = agentDefinitionId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("{agentDefinitionId:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid agentDefinitionId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteAgentDefinitionCommand { AgentDefinitionId = agentDefinitionId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

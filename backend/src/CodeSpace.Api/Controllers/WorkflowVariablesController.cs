using CodeSpace.Messages.Commands.Variables;
using CodeSpace.Messages.Queries.Variables;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for workflow-scoped variables. Each workflow has its own variable
/// namespace; the service validates the workflow belongs to the caller's current team
/// (404 conflated with not-yours, same pattern as repository / credential).
/// </summary>
[ApiController]
[Route("api/workflows/{workflowId:guid}/variables")]
public class WorkflowVariablesController : ControllerBase
{
    private readonly IMediator _mediator;

    public WorkflowVariablesController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List([FromRoute] Guid workflowId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListWorkflowVariablesQuery { WorkflowId = workflowId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpPut("{name}")]
    public async Task<IActionResult> Set([FromRoute] Guid workflowId, [FromRoute] string name, [FromBody] SetWorkflowVariableCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { WorkflowId = workflowId, Name = name }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("{name}")]
    public async Task<IActionResult> Delete([FromRoute] Guid workflowId, [FromRoute] string name, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteWorkflowVariableCommand { WorkflowId = workflowId, Name = name }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

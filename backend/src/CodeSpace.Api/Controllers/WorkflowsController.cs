using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the workflows engine. Every action binds its Command/Query record
/// directly via [FromBody] or [FromQuery] + a route-id merge via `with`.
/// </summary>
[ApiController]
[Route("api/workflows")]
public class WorkflowsController : ControllerBase
{
    private readonly IMediator _mediator;

    public WorkflowsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListWorkflowsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("{workflowId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid workflowId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowQuery { WorkflowId = workflowId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkflowCommand command, CancellationToken cancellationToken)
    {
        var id = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(new { id });
    }

    [HttpPut("{workflowId:guid}")]
    public async Task<IActionResult> Update([FromRoute] Guid workflowId, [FromBody] UpdateWorkflowCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { WorkflowId = workflowId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("{workflowId:guid}")]
    public async Task<IActionResult> Delete([FromRoute] Guid workflowId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new DeleteWorkflowCommand { WorkflowId = workflowId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("{workflowId:guid}/enabled")]
    public async Task<IActionResult> SetEnabled([FromRoute] Guid workflowId, [FromBody] SetWorkflowEnabledCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { WorkflowId = workflowId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>
    /// Operator-initiated "Run now". Body is the trigger payload — defaults to an empty
    /// object when null. Returns the new run id so the SPA can navigate to the run-detail
    /// page once the engine picks it up off the Hangfire queue.
    /// </summary>
    [HttpPost("{workflowId:guid}/run")]
    public async Task<IActionResult> Run([FromRoute] Guid workflowId, [FromBody] RunWorkflowManuallyCommand command, CancellationToken cancellationToken)
    {
        var runId = await _mediator.Send(command with { WorkflowId = workflowId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { runId });
    }

    [HttpGet("{workflowId:guid}/runs")]
    public async Task<IActionResult> ListRuns([FromRoute] Guid workflowId, [FromQuery] ListWorkflowRunsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { WorkflowId = workflowId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> GetRun([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowRunQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>
    /// Replay an existing run. Clones the original's workflow_version, release hash,
    /// trigger payload, and variable snapshot rows onto a fresh run id. Engine detects
    /// the existing snapshot and walks the replay path (plain frozen, secrets
    /// re-resolved). Returns the new run's id.
    /// </summary>
    [HttpPost("runs/{runId:guid}/replay")]
    public async Task<IActionResult> Replay([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var newRunId = await _mediator.Send(new ReplayRunCommand { OriginalRunId = runId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { runId = newRunId });
    }

    /// <summary>Editor palette: every loaded node type's manifest + JSON schemas.</summary>
    [HttpGet("node-manifests")]
    public async Task<IActionResult> ListNodeManifests([FromQuery] ListNodeManifestsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Canonical list of engine-injected <c>sys.*</c> variables (key + type + one-line
    /// description). Used by the editor's read-only System scope panel and the {{}}
    /// autocomplete picker — frontend no longer keeps a parallel hardcoded list.
    /// </summary>
    [HttpGet("system-variables")]
    public async Task<IActionResult> ListSystemVariables([FromQuery] ListSystemVariablesQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}

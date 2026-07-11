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

    /// <summary>
    /// Read a single workflow by a URL reference — its GUID (legacy link) or team-unique slug
    /// (canonical clean URL, e.g. <c>/api/workflows/nightly-audit</c>). The response carries the
    /// canonical <c>Slug</c> so the router can redirect a legacy-GUID URL to the slug URL. Mutations
    /// (PUT/DELETE/run) stay GUID-keyed — the caller already holds the id.
    /// </summary>
    [HttpGet("{idOrSlug}")]
    public async Task<IActionResult> Get([FromRoute] string idOrSlug, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowByRefQuery { IdOrSlug = idOrSlug }, cancellationToken).ConfigureAwait(false);
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

    // The RUN resource (detail · phases · replay · rerun-from-node · rerun-map-branch · resume · cancel · launch)
    // lives in WorkflowRunsController under the same api/workflows/runs prefix — one controller per resource.

    /// <summary>
    /// Plan a workflow from a free-text task. Returns <c>{ plannerEnabled, plan, definition }</c> — the planner
    /// emits a reviewable plan and a validated (but unsaved, unrun) definition the operator can save+run through
    /// the normal pipeline. When the planner is disabled, returns <c>{ plannerEnabled: false }</c> with no plan.
    /// Team comes from <c>ICurrentTeam</c> in the handler, never this body.
    /// </summary>
    [HttpPost("plan-from-task")]
    public async Task<IActionResult> PlanFromTask([FromBody] PlanWorkflowFromTaskCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
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

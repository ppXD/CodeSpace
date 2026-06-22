using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Queries.Tasks;
using CodeSpace.Messages.Queries.Workflows;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the RUN resource — the run-neutral execution every entry point shares (manual, scheduled,
/// provider webhook, child, replay, and task launch are all one <c>WorkflowRun</c> with an open <c>SourceType</c>).
/// Rooted under <c>api/workflows/runs</c> because the substrate IS the workflow engine, so one generic root is
/// honest rather than inventing a parallel resource. A run is addressed FLAT by its own id (<c>runs/{runId}</c>),
/// never nested under a workflow — a snapshot / task run has a null <c>WorkflowId</c>, so <c>{workflowId}/runs/{runId}</c>
/// could not address it. Split out of <see cref="WorkflowsController"/> (which keeps the authored-definition
/// resource) so each controller owns one resource. Every action is a thin mediator dispatch (Rule 16); team scope
/// + actor come from <c>ICurrentTeam</c> / <c>ICurrentUser</c> in the handlers, never the body.
/// </summary>
[ApiController]
[Route("api/workflows/runs")]
public class WorkflowRunsController : ControllerBase
{
    private readonly IMediator _mediator;

    public WorkflowRunsController(IMediator mediator) { _mediator = mediator; }

    /// <summary>Launch a run from a generic task spec (effort / autonomy / surface / repos). Returns the run id + route + projection.</summary>
    [HttpPost]
    public async Task<IActionResult> Launch([FromBody] LaunchTaskCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>One run's detail — status, per-node cells, version-pinned definition snapshot, outputs, pending wait. Team-scoped; foreign / absent → 404.</summary>
    [HttpGet("{runId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowRunQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The run's merged, Order-sorted phase tree (structural nodes + supervisor decisions) — the run-outline projection. Foreign / absent → 404.</summary>
    [HttpGet("{runId:guid}/phases")]
    public async Task<IActionResult> GetPhases([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetTaskRunPhasesQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>Replay an existing run onto a fresh run id (plain values frozen from snapshot, secrets re-resolved). Returns the new run id.</summary>
    [HttpPost("{runId:guid}/replay")]
    public async Task<IActionResult> Replay([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var newRunId = await _mediator.Send(new ReplayRunCommand { OriginalRunId = runId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { runId = newRunId });
    }

    /// <summary>Re-run an existing run STARTING FROM a chosen node (D7) — forks a run reusing the upstream cells, re-runs the node + downstream. Returns the new run id.</summary>
    [HttpPost("{runId:guid}/rerun-from-node")]
    public async Task<IActionResult> RerunFromNode([FromRoute] Guid runId, [FromBody] RerunRunFromNodeCommand command, CancellationToken cancellationToken)
    {
        var newRunId = await _mediator.Send(command with { OriginalRunId = runId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { runId = newRunId });
    }

    /// <summary>Re-run ONE branch of a top-level flow.map (D7) — forks a run reusing the N-1 sibling branches + the map's synthesizer. Returns the new run id.</summary>
    [HttpPost("{runId:guid}/rerun-map-branch")]
    public async Task<IActionResult> RerunMapBranch([FromRoute] Guid runId, [FromBody] RerunMapBranchCommand command, CancellationToken cancellationToken)
    {
        var newRunId = await _mediator.Send(command with { OriginalRunId = runId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { runId = newRunId });
    }

    /// <summary>Resolve a pending approval on a Suspended run (approve / reject + optional comment) and resume it. Returns <c>{ resumed }</c>.</summary>
    [HttpPost("{runId:guid}/resume")]
    public async Task<IActionResult> Resume([FromRoute] Guid runId, [FromBody] ResumeRunCommand command, CancellationToken cancellationToken)
    {
        var resumed = await _mediator.Send(command with { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { resumed });
    }

    /// <summary>Operator cancel: abort a non-terminal run + tear down its branch agents and staged children. Team-scoped; foreign → 404; already-terminal is an idempotent no-op.</summary>
    [HttpPost("{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var outcome = await _mediator.Send(new CancelRunCommand { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return outcome == null ? NotFound() : Ok(outcome);
    }
}

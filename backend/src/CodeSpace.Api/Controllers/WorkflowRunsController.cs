using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Queries.Sessions;
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

    /// <summary>The team's runs index — every top-level run the team owns (any source), newest first. Team-scoped.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListTeamRunsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>The runs cockpit's true scoped counts (the status cards) — live / failed / suspended / today over the bar's scope. Team-scoped.</summary>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] GetTeamRunSummaryQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

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

    /// <summary>The work-session thread this run belongs to, as a conversation anchored at the run's turn — for the run-detail → session view. Any run in the thread (a turn or a rerun attempt) resolves to the same thread. Team-scoped; session-less / foreign / absent → 404.</summary>
    [HttpGet("{runId:guid}/session")]
    public async Task<IActionResult> GetSession([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetSessionByRunQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The run's merged, Order-sorted phase tree (structural nodes + supervisor decisions) — the run-outline projection. Foreign / absent → 404.</summary>
    [HttpGet("{runId:guid}/phases")]
    public async Task<IActionResult> GetPhases([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetTaskRunPhasesQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The lineage's attempt ladder — the original run + every replay/rerun fork of it, oldest first, latest flagged. Drives the run-detail attempt switcher. Foreign / absent → 404.</summary>
    [HttpGet("{runId:guid}/attempts")]
    public async Task<IActionResult> GetAttempts([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRunAttemptsQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>One cell's attempt history — every lineage attempt that ran this (nodeId, iterationKey) cell, with its agent run + outcome. Lets a re-run node show each earlier run. Foreign / absent → 404.</summary>
    [HttpGet("{runId:guid}/cells/attempts")]
    public async Task<IActionResult> GetCellAttempts([FromRoute] Guid runId, [FromQuery] string nodeId, [FromQuery] string? iterationKey, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetCellAttemptsQuery { RunId = runId, NodeId = nodeId, IterationKey = iterationKey ?? string.Empty }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The run's narrative timeline — the merged, chronologically-sorted events across every source (run/node lifecycle, …). Foreign / absent → 404.</summary>
    [HttpGet("{runId:guid}/timeline")]
    public async Task<IActionResult> GetTimeline([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRunTimelineQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The run's RAW event ledger — every workflow_run_record row in Sequence order, unfiltered (the Trace audit, the unfiltered counterpart to /timeline). Foreign / absent → 404.</summary>
    [HttpGet("{runId:guid}/records")]
    public async Task<IActionResult> GetRecords([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRunRecordsQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Re-run a SET of a top-level flow.map's branches (the UI's "Rerun all failed items") in ONE fork. Returns <c>{ runId }</c>.</summary>
    [HttpPost("{runId:guid}/rerun-map-branches")]
    public async Task<IActionResult> RerunMapBranches([FromRoute] Guid runId, [FromBody] RerunMapBranchesCommand command, CancellationToken cancellationToken)
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

    /// <summary>Continue a STRANDED Suspended run (no pending wait) on demand — the user-triggered twin of the reconciler's re-dispatch. Team-scoped; foreign → 404. Returns <c>{ continued }</c>.</summary>
    [HttpPost("{runId:guid}/continue")]
    public async Task<IActionResult> Continue([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var continued = await _mediator.Send(new ContinueRunCommand { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { continued });
    }

    /// <summary>Operator cancel: abort a non-terminal run + tear down its branch agents and staged children. Team-scoped; foreign → 404; already-terminal is an idempotent no-op.</summary>
    [HttpPost("{runId:guid}/cancel")]
    public async Task<IActionResult> Cancel([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var outcome = await _mediator.Send(new CancelRunCommand { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return outcome == null ? NotFound() : Ok(outcome);
    }
}

using System.Text.Json;
using CodeSpace.Core.Services.Tasks.Trace;
using CodeSpace.Messages.Commands.Tasks;
using CodeSpace.Messages.Commands.Workflows;
using CodeSpace.Messages.Enums;
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
    private static readonly JsonSerializerOptions SseJson = new(JsonSerializerDefaults.Web);

    private readonly IMediator _mediator;
    private readonly IRunRecordStreamer _recordStreamer;

    public WorkflowRunsController(IMediator mediator, IRunRecordStreamer recordStreamer)
    {
        _mediator = mediator;
        _recordStreamer = recordStreamer;
    }

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

    /// <summary>
    /// One run's detail — status, per-node cells, version-pinned definition snapshot, outputs, pending wait.
    /// Addressed by a URL ref: the team-scoped run number (canonical clean URL, e.g. <c>runs/1042</c>) or a
    /// GUID (legacy link). The response carries <c>RunNumber</c> so the router can canonicalise a legacy-GUID
    /// URL to the number URL. Team-scoped; foreign / absent → 404. Sub-routes stay GUID-keyed (the FE resolves
    /// once here, then calls them with the run's id).
    /// </summary>
    [HttpGet("{idOrNumber}")]
    public async Task<IActionResult> Get([FromRoute] string idOrNumber, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetWorkflowRunByRefQuery { IdOrNumber = idOrNumber }, cancellationToken).ConfigureAwait(false);
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

    /// <summary>The run's CURRENT plan as a live checklist — the persisted contract (items + dependencies + acceptance) with per-item execution state derived from the durable tape. Foreign / absent / plan-less → 404.</summary>
    [HttpGet("{runId:guid}/plan")]
    public async Task<IActionResult> GetPlan([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRunWorkPlanQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>Answer the run's pending plan-confirmation card (triad S3): approve releases execution; a non-approve answer must carry revision feedback (400 otherwise) the supervisor folds into a revised plan version. No pending confirmation / foreign / absent → 404 (conflated).</summary>
    [HttpPost("{runId:guid}/plan/confirm")]
    public async Task<IActionResult> ConfirmPlan([FromRoute] Guid runId, [FromBody] ConfirmRunPlanCommand command, CancellationToken cancellationToken)
    {
        if (!command.Approve && string.IsNullOrWhiteSpace(command.Feedback)) return BadRequest(new { error = "Revision feedback is required when not approving the plan." });

        var result = await _mediator.Send(command with { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>Answer the run's NEWEST pending supervisor ask — any ask alike (a content question, a review-gate escalation where 'approve' is the one-shot absolution). Rides the same durable Action wait as the conversation card. Blank answer → 400; no pending ask / foreign / absent → 404 (conflated).</summary>
    [HttpPost("{runId:guid}/ask/answer")]
    public async Task<IActionResult> AnswerAsk([FromRoute] Guid runId, [FromBody] AnswerRunAskCommand command, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(command.Answer)) return BadRequest(new { error = "An answer is required." });

        var result = await _mediator.Send(command with { RunId = runId }, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Live SSE tail of the run's ledger — every workflow_run_record row beyond <c>?after={sequence}</c> as it lands (interaction.delta + lifecycle), so the Room streams live instead of re-polling. <c>id:</c> = Sequence (for Last-Event-ID / <c>?after=</c> gapless resume), <c>event:</c> = the record type. Ends at a terminal run record or on client disconnect. Team-scoped; a foreign run streams nothing. The 2s poll (GET /records) stays the fallback.</summary>
    [HttpGet("{runId:guid}/records/stream")]
    public async Task StreamRecords([FromRoute] Guid runId, [FromQuery] long after, CancellationToken cancellationToken)
    {
        Response.Headers.ContentType = "text/event-stream";
        Response.Headers.CacheControl = "no-cache";
        Response.Headers["X-Accel-Buffering"] = "no";   // ask an nginx-style proxy not to buffer the stream

        await foreach (var record in _recordStreamer.TailAsync(runId, after, cancellationToken).ConfigureAwait(false))
        {
            var data = JsonSerializer.Serialize(record, SseJson);
            await Response.WriteAsync($"id: {record.Sequence}\nevent: {record.RecordType}\ndata: {data}\n\n", cancellationToken).ConfigureAwait(false);
            await Response.Body.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Replay an existing run onto a fresh run id (plain values frozen from snapshot, secrets re-resolved). Returns the new run id.</summary>
    [HttpPost("{runId:guid}/replay")]
    public async Task<IActionResult> Replay([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var newRunId = await _mediator.Send(new ReplayRunCommand { OriginalRunId = runId }, cancellationToken).ConfigureAwait(false);
        return Ok(new { runId = newRunId });
    }

    /// <summary>The Room's "Open PR" action (PR-6): opens (or, on a repeat call, reuses) a pull/merge request for a terminal run's published branch(es). Team-scoped; foreign → 404. Returns <c>{ pullRequests }</c> — one entry per repository the run published to.</summary>
    [HttpPost("{runId:guid}/open-pull-request")]
    public async Task<IActionResult> OpenPullRequest([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new OpenRunPullRequestCommand { WorkflowRunId = runId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
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

    /// <summary>Operator override: force-resolve a STRANDED signal-driven wait (a dropped Timer wake / dead Callback) on a Suspended run to un-strand it. Refuses a decision/completion wait (409). Team-scoped; foreign → 404. Returns <c>{ outcome, reissued }</c>.</summary>
    [HttpPost("{runId:guid}/waits/{waitId:guid}/reissue")]
    public async Task<IActionResult> ReissueWait([FromRoute] Guid runId, [FromRoute] Guid waitId, [FromBody] ReissueWaitCommand command, CancellationToken cancellationToken)
    {
        var outcome = await _mediator.Send(command with { RunId = runId, WaitId = waitId }, cancellationToken).ConfigureAwait(false);

        return outcome == ReissueWaitOutcome.UnsupportedKind
            ? Conflict(new { outcome = outcome.ToString(), reissued = false })
            : Ok(new { outcome = outcome.ToString(), reissued = outcome == ReissueWaitOutcome.Reissued });
    }

    /// <summary>Continue a run IN PLACE (same run id, never a fork): a STRANDED Suspended run (the reconciler's re-dispatch twin), a terminal FAILURE run (re-run the halting node[s]), or a terminal CANCELLED run the operator stopped mid-flight (re-run the interrupted frontier). Team-scoped; foreign → 404. Returns <c>{ continued }</c> — false when the state can't continue in place (Success/Running, or nothing incomplete to resume) so the caller falls back to replay / rerun.</summary>
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

using CodeSpace.Messages.Commands.Sessions;
using CodeSpace.Messages.Queries.Sessions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the SESSION resource — a work thread (one <c>WorkSession</c>) the user reads as a conversation of
/// turns (each turn = one run). Rooted FLAT at <c>api/sessions</c>; team scope comes from <c>ICurrentTeam</c> in the
/// handlers, never the body. READ-ONLY — a thread is CONTINUED by launching a run with its <c>sessionId</c>
/// (<c>POST api/workflows/runs</c>), and the run-anchored thread lookup is <c>api/workflows/runs/{runId}/session</c>, so
/// there is no write action here. Every action is a thin mediator dispatch (Rule 16).
/// </summary>
[ApiController]
[Route("api/sessions")]
public class SessionsController : ControllerBase
{
    private readonly IMediator _mediator;

    public SessionsController(IMediator mediator) { _mediator = mediator; }

    /// <summary>The team's sessions index — every work thread, most-recently-active first, keyset-paginated. Team-scoped.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] ListTeamSessionsQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>One thread as a conversation — its turns (each turn = a run; reruns nested as attempts). Team-scoped; foreign / absent → 404.</summary>
    [HttpGet("{sessionId:guid}")]
    public async Task<IActionResult> Get([FromRoute] Guid sessionId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetSessionDetailQuery { SessionId = sessionId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The backend-authored Session Room for the session a run belongs to, focused on that run's turn — the AI work transcript the frontend renders by block type. Team-scoped; foreign / session-less / absent → 404.</summary>
    [HttpGet("by-run/{runId:guid}/room")]
    public async Task<IActionResult> RunRoom([FromRoute] Guid runId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetRunRoomQuery { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The Session Room for a session, focused on <c>focusRunId</c>'s turn when given (else the latest turn). Team-scoped; foreign / absent → 404.</summary>
    [HttpGet("{sessionId:guid}/room")]
    public async Task<IActionResult> SessionRoom([FromRoute] Guid sessionId, [FromQuery] GetSessionRoomQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { SessionId = sessionId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The backend-authored Session JOURNAL (the chronological work transcript) for the session a run belongs to, focused on that run's turn. <c>?since=</c> returns only the steps after the cursor (the live delta). Team-scoped; foreign / session-less / absent → 404.</summary>
    [HttpGet("by-run/{runId:guid}/journal")]
    public async Task<IActionResult> RunJournal([FromRoute] Guid runId, [FromQuery] GetRunJournalQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { RunId = runId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>The Session Journal for a session, focused on <c>focusRunId</c>'s turn when given (else the latest). <c>?since=</c> returns only the steps after the cursor. Team-scoped; foreign / absent → 404.</summary>
    [HttpGet("{sessionId:guid}/journal")]
    public async Task<IActionResult> SessionJournal([FromRoute] Guid sessionId, [FromQuery] GetSessionJournalQuery query, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(query with { SessionId = sessionId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>One model call's full detail (prompt · result · usage · trace) — the journal's model-call drawer, fetched on demand by the completed interaction record's ledger sequence. Team-scoped; foreign / absent run or unknown sequence → 404.</summary>
    [HttpGet("by-run/{runId:guid}/model-call/{sequence:long}")]
    public async Task<IActionResult> RunModelCall([FromRoute] Guid runId, [FromRoute] long sequence, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetModelCallDetailQuery { RunId = runId, Sequence = sequence }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>A generic preview of one file the run's turn produced (from the producing agent's captured diff), keyed by repo-relative path. Optional <c>agentRunId</c> scopes to one agent's version (per-agent attribution). Team-scoped; foreign / absent run → 404.</summary>
    [HttpGet("by-run/{runId:guid}/room/file")]
    public async Task<IActionResult> RunRoomFile([FromRoute] Guid runId, [FromQuery] string path, [FromQuery] Guid? agentRunId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new GetSessionRoomFileQuery { RunId = runId, Path = path, AgentRunId = agentRunId }, cancellationToken).ConfigureAwait(false);
        return result == null ? NotFound() : Ok(result);
    }

    /// <summary>Rename a session's thread title (the body carries the new title; the route is the authoritative id). Team-scoped; foreign / absent → 404.</summary>
    [HttpPatch("{sessionId:guid}")]
    public async Task<IActionResult> Rename([FromRoute] Guid sessionId, [FromBody] RenameSessionCommand command, CancellationToken cancellationToken)
    {
        var renamed = await _mediator.Send(command with { SessionId = sessionId }, cancellationToken).ConfigureAwait(false);
        return renamed ? NoContent() : NotFound();
    }
}

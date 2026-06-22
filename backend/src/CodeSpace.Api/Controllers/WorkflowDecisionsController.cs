using CodeSpace.Messages.Commands.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Queries.Decisions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the cross-grain "Needs decision" queue, rooted under <c>api/workflows/decisions</c> so every
/// run-related read shares the one generic <c>api/workflows</c> root. The queue is team-wide (a per-run view is a
/// future <c>runs/{runId}/decisions</c> filter); team scope comes from <c>X-Team-Id</c> and the MediatR pipeline
/// vets membership before the handler runs (<c>IRequireTeamMembership</c>).
/// </summary>
[ApiController]
[Route("api/workflows/decisions")]
public class WorkflowDecisionsController : ControllerBase
{
    private readonly IMediator _mediator;

    public WorkflowDecisionsController(IMediator mediator) { _mediator = mediator; }

    /// <summary>The team's pending decisions across both grains (agent.code mid-run + flow.decision node), soonest-deadline first.</summary>
    [HttpGet]
    public async Task<IActionResult> ListPending(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListPendingDecisionsQuery(), cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>Answer a pending decision (either grain) — resolves the agent's mid-run call or resumes the workflow. The route id is the authority (Rule 17); the answer (selected options / free text) is the body.</summary>
    [HttpPost("{decisionId:guid}/answer")]
    public async Task<IActionResult> Answer([FromRoute] Guid decisionId, [FromBody] AnswerDecisionCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { DecisionId = decisionId }, cancellationToken).ConfigureAwait(false);

        return result.Outcome switch
        {
            DecisionAnswerOutcome.Answered => Ok(result),
            DecisionAnswerOutcome.AlreadyResolved => Conflict(result),
            DecisionAnswerOutcome.Invalid => BadRequest(result),
            _ => NotFound(result),
        };
    }
}

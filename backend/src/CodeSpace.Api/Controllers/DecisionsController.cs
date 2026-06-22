using CodeSpace.Messages.Commands.Decisions;
using CodeSpace.Messages.Dtos.Decisions;
using CodeSpace.Messages.Queries.Decisions;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// REST surface for the cross-grain "Needs decision" queue (Decision substrate D3). Team scope comes from
/// <c>X-Team-Id</c>; the MediatR pipeline vets membership before the handler runs (<c>IRequireTeamMembership</c>).
/// </summary>
[ApiController]
[Route("api/decisions")]
public class DecisionsController : ControllerBase
{
    private readonly IMediator _mediator;

    public DecisionsController(IMediator mediator) { _mediator = mediator; }

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

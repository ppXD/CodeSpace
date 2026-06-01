using CodeSpace.Messages.Queries.Users;
using MediatR;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// Team-scoped resources keyed off <c>X-Team-Id</c>. Currently the member directory — the
/// identity lookup the chat UI uses to name message authors and drive the <c>@</c>-mention
/// picker. The MediatR pipeline vets that the caller belongs to the team before the handler runs.
/// </summary>
[ApiController]
[Route("api/teams")]
public class TeamsController : ControllerBase
{
    private readonly IMediator _mediator;

    public TeamsController(IMediator mediator) { _mediator = mediator; }

    [HttpGet("members")]
    public async Task<IActionResult> Members(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListTeamMembersQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Member identities for DISPLAY — INCLUDES the team's CodeSpace bot so the chat UI can name a
    /// message authored by the bot. Distinct from <see cref="Members"/> (which excludes bots) so the
    /// @-mention picker / roster stay human-only.
    /// </summary>
    [HttpGet("member-identities")]
    public async Task<IActionResult> MemberIdentities(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListTeamMemberIdentitiesQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }
}

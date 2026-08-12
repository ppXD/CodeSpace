using CodeSpace.Messages.Commands.Invitations;
using CodeSpace.Messages.Commands.Teams;
using CodeSpace.Messages.Queries.Invitations;
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

    /// <summary>
    /// Invite an address to this team. The link comes back ONCE — it is not stored, and a member who
    /// loses it regenerates rather than reads it again.
    /// </summary>
    [HttpPost("invitations")]
    public async Task<IActionResult> Invite([FromBody] CreateTeamInvitationCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpGet("invitations")]
    public async Task<IActionResult> Invitations(CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new ListTeamInvitationsQuery(), cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    [HttpDelete("invitations/{invitationId:guid}")]
    public async Task<IActionResult> RevokeInvitation([FromRoute] Guid invitationId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new RevokeTeamInvitationCommand { InvitationId = invitationId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>Replaces the token, which kills the previous link — the move for a link that went astray.</summary>
    [HttpPost("invitations/{invitationId:guid}/regenerate")]
    public async Task<IActionResult> RegenerateInvitation([FromRoute] Guid invitationId, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new RegenerateTeamInvitationCommand { InvitationId = invitationId }, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>Move someone between roles. The server clamps both ways — see TeamMemberService.</summary>
    [HttpPatch("members/{userId:guid}")]
    public async Task<IActionResult> ChangeMemberRole([FromRoute] Guid userId, [FromBody] ChangeTeamMemberRoleCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { UserId = userId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpDelete("members/{userId:guid}")]
    public async Task<IActionResult> RemoveMember([FromRoute] Guid userId, CancellationToken cancellationToken)
    {
        await _mediator.Send(new RemoveTeamMemberCommand { UserId = userId }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    /// <summary>The caller leaving. Separate from removing someone else because it needs no permission.</summary>
    [HttpPost("members/leave")]
    public async Task<IActionResult> Leave(CancellationToken cancellationToken)
    {
        await _mediator.Send(new LeaveTeamCommand(), cancellationToken).ConfigureAwait(false);
        return NoContent();
    }

    [HttpPost("transfer-ownership")]
    public async Task<IActionResult> TransferOwnership([FromBody] TransferTeamOwnershipCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

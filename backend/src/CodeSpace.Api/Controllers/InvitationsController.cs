using CodeSpace.Messages.Commands.Invitations;
using CodeSpace.Messages.Queries.Invitations;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace CodeSpace.Api.Controllers;

/// <summary>
/// The two surfaces an invitee touches before they have an account.
///
/// <para><see cref="AllowAnonymousAttribute"/> on both: the person opening the link has no session,
/// and the global fallback policy would otherwise answer a 401 that sends them to sign in for an
/// account they do not have. The token in the route is the credential, and it never appears in a
/// query string, where it would reach proxy logs and browser history.</para>
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("api/invitations")]
public class InvitationsController : ControllerBase
{
    private readonly IMediator _mediator;

    public InvitationsController(IMediator mediator) { _mediator = mediator; }

    /// <summary>What the link is worth. Answers only for a token that checks out — see the query's doc.</summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Preview([FromRoute] string token, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(new PreviewInvitationQuery { Token = token }, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }

    /// <summary>Spends the invitation and answers with a session, so the invitee lands signed in.</summary>
    [HttpPost("{token}/accept")]
    public async Task<IActionResult> Accept([FromRoute] string token, [FromBody] AcceptInvitationCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command with { Token = token }, cancellationToken).ConfigureAwait(false);

        return Ok(result);
    }
}

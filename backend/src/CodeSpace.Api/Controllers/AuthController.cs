using CodeSpace.Messages.Commands.Accounts;
using CodeSpace.Messages.Commands.Auth;
using MediatR;
using Microsoft.AspNetCore.Authorization;
using CodeSpace.Api.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace CodeSpace.Api.Controllers;

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IMediator _mediator;

    public AuthController(IMediator mediator) { _mediator = mediator; }

    // [FromBody] CommandType directly — no per-endpoint Request DTO. The MediatR command
    // IS the wire contract; with JsonStringEnumConverter wired globally + X-Team-Id moving
    // to the request header, there's no field on these commands the client shouldn't see.

    [HttpPost("sign-in")]
    [AllowAnonymous]
    public async Task<IActionResult> SignIn([FromBody] SignInCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    // change-password requires the bearer token (the user proves they are who they say
    // they are by also presenting CurrentPassword). The MediatR pipeline lets it through
    // even when password_must_change=true via the IBypassPasswordRotationGuard marker.
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);
        return Ok(result);
    }

    /// <summary>
    /// Spend a password-reset link. Anonymous: someone who cannot sign in is exactly who needs this,
    /// and requiring a session would make the link useless to them.
    /// </summary>
    [AllowAnonymous]
    [EnableRateLimiting(AnonymousRateLimitExtension.PolicyName)]
    [HttpPost("reset-password/{token}")]
    public async Task<IActionResult> ResetPassword([FromRoute] string token, [FromBody] ResetPasswordCommand command, CancellationToken cancellationToken)
    {
        await _mediator.Send(command with { Token = token }, cancellationToken).ConfigureAwait(false);
        return NoContent();
    }
}

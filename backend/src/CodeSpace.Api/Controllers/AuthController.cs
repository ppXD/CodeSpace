using CodeSpace.Core.Services.Auth;
using CodeSpace.Messages.Commands.Accounts;
using CodeSpace.Messages.Commands.Auth;
using MediatR;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
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

    // Every endpoint that mints a session hands it to the browser in the HttpOnly session cookie, which its scripts never
    // see (SessionCookie), and to a caller that keeps it itself — an API client, the CLI — in the body as well. Never in
    // the body to a request the cookie carried: that caller may be a script in the page, and it could read a body.

    [HttpPost("sign-in")]
    [AllowAnonymous]
    public async Task<IActionResult> SignIn([FromBody] SignInCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);

        SessionCookie.Issue(HttpContext, result.Token!, result.ExpiresAt);

        return Ok(SessionCookie.Authenticated(HttpContext) ? result with { Token = null } : result);
    }

    // change-password requires the bearer token (the user proves they are who they say
    // they are by also presenting CurrentPassword). The MediatR pipeline lets it through
    // even when password_must_change=true via the IBypassPasswordRotationGuard marker.
    // The change rotates the stamp, killing the cookie this request may have ridden in on, so the
    // browser is handed the session minted under the new one.
    [HttpPost("change-password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordCommand command, CancellationToken cancellationToken)
    {
        var result = await _mediator.Send(command, cancellationToken).ConfigureAwait(false);

        SessionCookie.Issue(HttpContext, result.Token!, result.ExpiresAt);

        return Ok(SessionCookie.Authenticated(HttpContext) ? result with { Token = null } : result);
    }

    // The browser's session as a resource. No mediator: authentication has already happened by the time an action runs,
    // and what is left is setting or clearing a cookie, which is HTTP and nothing else. All three require a live
    // session (the fallback policy), and the two writes, ridden in on the cookie, require its CSRF header.

    /// <summary>"Am I signed in?" — answered by whatever credential the request carries. The SPA asks without a header, so a 204 proves the browser holds and sends the cookie.</summary>
    [HttpGet("session")]
    public IActionResult ProbeSession() => NoContent();

    /// <summary>
    /// Moves the presented token into the session cookie: how a browser signed in before the cookie existed trades the
    /// token its scripts held for one they cannot. It is the SAME token — same expiry, same stamp — so the exchange can
    /// never extend a session, and it never appears in the response body.
    /// </summary>
    [HttpPost("session")]
    public async Task<IActionResult> EstablishSession()
    {
        var token = await HttpContext.GetTokenAsync(JwtBearerDefaults.AuthenticationScheme, "access_token").ConfigureAwait(false);

        SessionCookie.Issue(HttpContext, token!, BearerTokenLifetime.ExpiresAt(User)!.Value);

        return NoContent();
    }

    /// <summary>Signs this browser out by clearing its cookie. Other sessions stay live: ending every session is what changing the password does.</summary>
    [HttpDelete("session")]
    public IActionResult EndSession()
    {
        SessionCookie.Clear(HttpContext);

        return NoContent();
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
